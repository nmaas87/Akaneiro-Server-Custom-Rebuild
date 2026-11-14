using System.Reflection;
using Akaneiro.Server.Data;
using Akaneiro.Server.Game;
using Akaneiro.Server.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;
using Vector3 = UnityEngine.Vector3;

namespace Akaneiro.Server.Tests.Harness;

/// <summary>A message the server sent, as the client's own parser delivered it (callback name + arguments).</summary>
public sealed record Message(string Name, object[] Args)
{
    public T Arg<T>(int index) => (T)Args[index];
}

/// <summary>
/// A game client wired straight into a server session, without sockets. Requests are built with the
/// client's ProtocolX_SendRequest, answers are parsed with the client's ProtocolX_ReceiveCallback,
/// so the wire format is checked exactly as the game sees it.
///
/// Everything the server does in reaction to a request has happened when the request returns, so
/// tests never wait: they look at what arrived. Time only passes through <see cref="Wait"/>.
/// </summary>
public sealed class TestPlayer : IPacketSink, IDisposable
{
    private static int _counter;

    private readonly List<Message> _inbox = new();
    private readonly List<string> _errors = new();
    private ProtocolGame_ICallbackReceiver _gameReceiver;
    private ProtocolBattle_ICallbackReceiver _battleReceiver;
    private readonly string _dataDir;

    public ManualClock Clock { get; private set; } = new();
    public World World { get; }
    public Session Session { get; }

    public CharacterData Character => Session.Character;
    public SceneInstance Scene => Session.Scene;

    /// <summary>Abilities and gear the server sent when the character was selected.</summary>
    public SPlayerSkillInfo Skills { get; private set; }
    public SBagGroup Bags { get; private set; }
    public SCharacterInfoBasic Selected { get; private set; }

    public TestPlayer(ITestOutputHelper output = null, int seed = 1, bool webLogin = false, int autoSaveSeconds = 180)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "akaneiro-tests", Guid.NewGuid().ToString("N"));
        World = new World(Options.Create(new ServerOptions { LogPackets = false, WebLogin = webLogin, AutoSaveSeconds = autoSaveSeconds }),
            new AccountStore(_dataDir), TestData.Data, new WebUserStore(_dataDir))
        {
            Clock = Clock,
            Random = new Random(seed),
        };
        Session = new Session(World, this, new OutputLogger(output, _errors));
        WireReceivers();
    }

    /// <summary>A second client on the same server (same accounts, same clock): the reconnect
    /// and one-session-per-account tests drive two of these against one World.</summary>
    public TestPlayer(World world, ITestOutputHelper output = null)
    {
        _dataDir = null; // the World belongs to whoever created it
        World = world;
        Clock = (ManualClock)world.Clock; // one clock for the whole server
        Session = new Session(World, this, new OutputLogger(output, _errors));
        WireReceivers();
    }

    private void WireReceivers()
    {
        _gameReceiver = DispatchProxy.Create<ProtocolGame_ICallbackReceiver, Recorder>();
        ((Recorder)(object)_gameReceiver).Inbox = _inbox;
        _battleReceiver = DispatchProxy.Create<ProtocolBattle_ICallbackReceiver, Recorder>();
        ((Recorder)(object)_battleReceiver).Inbox = _inbox;
    }

    // ---- talking to the server ------------------------------------------------------------------

    /// <summary>Sends a request built with the client's ProtocolGame/ProtocolBattle_SendRequest.</summary>
    public void Send(Packet request)
    {
        byte[] bytes = request.GetByteStream();
        Session.HandlePacket(bytes[6..]); // without the int32 size + int16 type header
        ThrowServerErrors();
    }

    /// <summary>What the server sends lands here and is parsed the way the client parses it.</summary>
    void IPacketSink.Send(Packet packet)
    {
        byte[] body = packet.GetByteStream()[6..];
        if (!ProtocolGame_ReceiveCallback.Parse(_gameReceiver, new Packet(body)) &&
            !ProtocolBattle_ReceiveCallback.Parse(_battleReceiver, new Packet(body)))
            throw new InvalidOperationException("the client could not parse a packet the server sent");
    }

    /// <summary>A "closed" test client simply stops receiving; the session end is driven explicitly.</summary>
    void IPacketSink.Close() => Closed = true;

    /// <summary>True once the server ended this test connection (reconnect takeover).</summary>
    public bool Closed { get; private set; }

    /// <summary>Lets game time pass; monsters act, cooldowns run out, ability objects expire.</summary>
    public void Wait(double seconds)
    {
        Clock.Advance(TimeSpan.FromSeconds(seconds));
        ThrowServerErrors();
    }

    /// <summary>The server catches and logs exceptions; a test must not miss them.</summary>
    private void ThrowServerErrors()
    {
        if (_errors.Count == 0)
            return;
        string errors = string.Join(Environment.NewLine, _errors);
        _errors.Clear();
        throw new Xunit.Sdk.XunitException("the server logged errors: " + errors);
    }

    // ---- looking at the answers -----------------------------------------------------------------

    public IReadOnlyList<Message> Inbox => _inbox;

    public bool Has(string name, Func<Message, bool> where = null) => _inbox.Any(m => m.Name == name && (where == null || where(m)));

    /// <summary>Takes the first message of this kind out of the inbox; fails if none arrived.</summary>
    public Message Take(string name, Func<Message, bool> where = null)
    {
        int index = _inbox.FindIndex(m => m.Name == name && (where == null || where(m)));
        if (index < 0)
            throw new Xunit.Sdk.XunitException($"expected {name}{(where != null ? " (matching)" : "")}, received: {Describe()}");
        Message message = _inbox[index];
        _inbox.RemoveAt(index);
        return message;
    }

    /// <summary>Takes every message of this kind out of the inbox.</summary>
    public List<Message> TakeAll(string name, Func<Message, bool> where = null)
    {
        var taken = _inbox.Where(m => m.Name == name && (where == null || where(m))).ToList();
        _inbox.RemoveAll(taken.Contains);
        return taken;
    }

    public T Take<T>(string name, int argument = 0) => Take(name).Arg<T>(argument);

    public void ClearInbox() => _inbox.Clear();

    public string Describe() =>
        _inbox.Count == 0 ? "nothing" : string.Join(", ", _inbox.GroupBy(m => m.Name).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));

    // ---- common steps ---------------------------------------------------------------------------

    /// <summary>Logs in with a new account and creates and selects a character, like a new player.</summary>
    public TestPlayer LoginNewCharacter(int sex = ESex.eSex_Female, int style = 4)
    {
        int n = Interlocked.Increment(ref _counter);
        return LoginAs("7656119" + n.ToString("D10"), sex, style);
    }

    /// <summary>The bare login request (no assertions): the reconnect handshake tests take the
    /// UserLoginOK or UserReconnectNotify answer themselves.</summary>
    public void SendLogin(string account) =>
        Send(ProtocolGame_SendRequest.UserLogin(account, new EUserType(EUserType.eUserType_Steam),
            new EClientType(EClientType.eClientType_Standalone_Win), "", "token"));

    /// <summary>Login + create + select on a chosen account string.</summary>
    public TestPlayer LoginAs(string account, int sex = ESex.eSex_Female, int style = 4)
    {
        SendLogin(account);
        Take("On_UserLoginOK");

        var create = new SCharacterCreationData
        {
            nickname = "Tester" + Interlocked.Increment(ref _counter),
            style = style,
            avatarTraits = new mapIntInt(),
            sex = new ESex(sex),
        };
        Send(ProtocolGame_SendRequest.UserCreateCharacter(create));
        var created = Take<SCharacterInfoLogin>("On_UserCreateCharacterOK");

        Send(ProtocolGame_SendRequest.UserSelectCharacter(created.ID));
        Selected = Take<SCharacterInfoBasic>("On_UserSelectCharacterOK");
        Bags = Take<SBagGroup>("On_SendBagInfo");
        Skills = Take<SPlayerSkillInfo>("On_SendSkillInfo");
        return this;
    }

    public void AcceptMission(int missionId, int npcId = 1001)
    {
        Send(ProtocolGame_SendRequest.AcceptMission(npcId, missionId));
        Take("On_AcceptMissionOk");
    }

    /// <summary>
    /// Pretends the character has already finished these missions: the server only lets a
    /// mission be accepted when its map is unlocked (MissionMap.Unlocked), and scene-mechanic
    /// tests pick maps further into the progression. Seeding the completed list says exactly
    /// that: "this character has cleared this map".
    /// </summary>
    public void Unlock(params int[] missionIds)
    {
        foreach (int id in missionIds)
            if (!Character.CompletedMissions.Contains(id))
                Character.CompletedMissions.Add(id);
    }

    /// <summary>Loads a scene the way the client does; returns the start info. Other answers stay in the inbox.</summary>
    public SCharacterStartInfo EnterScene(string map)
    {
        Send(ProtocolBattle_SendRequest.EnterScene(map));
        Take("On_EnterSceneOk");
        Send(ProtocolBattle_SendRequest.MapLoadOK());
        return Take<SCharacterStartInfo>("On_CharacterStartInfo");
    }

    /// <summary>Enters the tutorial village and walks up to the first wolf, which wakes its trigger (246).</summary>
    public SCharacterStartInfo EnterTutorial()
    {
        var start = EnterScene("Hub_Village_Tutorial");
        MoveTo(Scene.TriggersById(246).Single().Center);
        return start;
    }

    public void MoveTo(Vector3 position) => Send(ProtocolBattle_SendRequest.UpdateMoveRequest(position, position, 0f, 0));

    public void MoveTo(float[] position) => MoveTo(new Vector3(position[0], position[1], position[2]));

    /// <summary>One basic attack; returns the player's own attack result.</summary>
    public SUseSkillResult Attack(int objectId)
    {
        Send(ProtocolBattle_SendRequest.UseNormalAttack(new EWeaponType(EWeaponType.mainWeapon), (uint)objectId, 0));
        Wait(Akaneiro.Server.Game.BattleHandler.MaxSwingHit); // the result comes when the swing lands
        return Take("On_UseNormalAttackResult", m => m.Arg<SUseSkillResult>(1).sourceObjectID == Session.PlayerObjectId)
            .Arg<SUseSkillResult>(1);
    }

    /// <summary>Attacks a monster until it is dead; returns the number of hits it took.</summary>
    public int Kill(int objectId, int maxHits = 200)
    {
        for (int hits = 1; hits <= maxHits; hits++)
        {
            Attack(objectId);
            if (Scene.Find(objectId) == null)
                return hits;
        }
        throw new Xunit.Sdk.XunitException($"monster {objectId} still alive after {maxHits} hits");
    }

    public void UseAbility(int abilityId, Vector3 location, int target = 0) =>
        Send(ProtocolBattle_SendRequest.UseFightSkill((uint)abilityId, (uint)target, location, 0));

    /// <summary>Monsters the client has been told about (On_monsterEnter), without taking them from the inbox.</summary>
    public List<SMonsterEnter> AnnouncedMonsters() =>
        _inbox.Where(m => m.Name == "On_monsterEnter").Select(m => m.Arg<SMonsterEnter>(0)).ToList();

    public void Dispose()
    {
        Session.OnDisconnected();
        if (_dataDir != null)
            try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    /// <summary>Implements the client's callback interfaces and records every call.</summary>
    public class Recorder : DispatchProxy
    {
        public List<Message> Inbox;

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            Inbox.Add(new Message(targetMethod.Name, args));
            return null;
        }
    }

    /// <summary>Server log lines go to the test output, which xUnit shows for failed tests.</summary>
    private sealed class OutputLogger : ILogger
    {
        private readonly ITestOutputHelper _output;
        private readonly List<string> _errors;

        public OutputLogger(ITestOutputHelper output, List<string> errors)
        {
            _output = output;
            _errors = errors;
        }

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            string line = formatter(state, exception) + (exception != null ? " " + exception : "");
            if (logLevel >= LogLevel.Error)
                _errors.Add(line);
            if (_output == null)
                return;
            try { _output.WriteLine(line); }
            catch (InvalidOperationException) { } // test already finished
        }
    }
}
