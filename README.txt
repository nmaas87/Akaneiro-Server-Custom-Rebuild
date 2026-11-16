AKANEIRO PRIVATE SERVER
=======================

A replacement server for Akaneiro: Demon Hunters (Steam build, Unity 4). The official servers are
gone and the original server code is lost; this server is rebuilt from the client source that
Spicy Horse released into the public domain (https://github.com/SpicyHorseLegacy/akaneiro-client),
so the unmodified Steam game can be played again on your own computer.

- How to set it up and play: HOSTING.txt
- What does not work (yet), and the assumptions made where the original rules are lost:
  KNOWN-ISSUES.txt

The game client is never changed. Everything happens on the server side: a stand-in for the
game's old login service (mw.angry-red.com) and the game server itself.


SUPPORTED CLIENT VERSIONS
-------------------------

    20140416 English - the Steam build (and its standalone fat client)
        The main target, full support: everything in WHAT WORKS below.

    20130416 English - the "04162" fat client (win04162.zip)
        Playable. It speaks the April 2013 cut of the protocol; the server translates every
        packet to that dialect and plays the session on the era's own content tables (items,
        level curve, daily rewards read from the client's own bundle). Actively polished; the
        features the cut itself never shipped (mail, stash, crafting UI, redeem gifts) are
        absent by design - see KNOWN-ISSUES.txt.

    20130130 Chinese (ZH) - the oldest fat client
        Playable. Login, tutorial, bounty map, missions, combat and revival are played through
        end to end on an untouched build. The server speaks its January 2013 protocol cut
        (dropping messages it has no parser for, rewriting structs to its older field lists,
        and renumbering error codes). Verified against that untouched build: even though the
        Chinese service shipped this client as late as August 2014, its protocol classes never
        picked up the April 2013 or 2014 additions - it needs its own dialect, not the April
        2013 one. Its bounty panel speaks its own id families: the mission list carries
        threat-suffixed ids (5xy1) that arm its slots, the region push carries whole-region ids
        (5R00) that light its map icons, and region unlocks and in-place revivals charge the
        price ladders of the tables bundled inside the build itself (MissionArea.Area,
        RevivalCost.Costs). Its mission map is limited to the fifteen maps that build bundles
        (Act 1-5, three per region): the fourth-mission maps and Acts 6-8 stay dark, so it
        never offers a level its client cannot load. Like the April build it opens its tutorial
        doors by wolf-kill milestones, so its tutorial spawns the era's 29-wolf waves instead
        of the 2014 layout's 17 (which can never finish), and it stands up after a revive
        through the innate 12001 shockwave state. The remaining rough edges come from serving
        it from the 2014 content tables: a drop or gift that postdates its own item bundle
        shows without a picture, and daily gifts arrive without a gift screen (its handlers for
        that window are empty stubs) - see KNOWN-ISSUES.txt.

Downloads of the retired standalone clients (unofficial community archives on archive.org):

    20130130 Chinese:                       https://archive.org/details/AkaneiroDemonHunters-LivBs
    20130416 English (04162) and 20140416:  https://archive.org/details/AkaneiroDemonHunters

If you can find a 20130130 build in English, please open an issue (or otherwise let me know) -
it would close the one gap in the version matrix above.

The fat clients log in with an account name and password instead of Steam: while the server is
running, create one at http://127.0.0.1/register.html, then log in and create your character.
(The Steam build creates its account automatically from your Steam session - see QUICK START.)


WHAT WORKS
----------

Login through Steam, characters, the village with its NPCs, the tutorial, the mission map with
unlocking and threat levels, every mission with its monsters and their AI, combat, abilities,
critical hits, karma, loot, levels and talents, death and revival, level events (trigger volumes,
spawner waves, gates, portals, switches and bridges, levers, baskets and chests, speech bubbles,
mission tasks), kill chains, wanted bounties, inventory and equipment, the vendors, the ability
trainer and masteries, the stash, the well, spirit helpers, hireable companion allies (the
summon window; no shipped client build runs that window - the whole ally chain is stubbed out
client-side, in the Steam build as much as the web one - see KNOWN-ISSUES), the mailbox with the
welcome letters, daily rewards and crafting. The 2013 Chinese and April 2013 English standalone
builds also play over the web login; the server answers each in its own older packet dialect
and plays April 2013 sessions on that era's own content tables - items, level curve and daily
rewards from the client's own bundle, with its mission id family translated on the wire
(see HOSTING.txt).

Every player gets their own single-player world. Several players can use one server at the same
time (for example in a LAN), but they do not see each other. One account plays on one connection
at a time: logging in a second time hands the account over, closing the old connection (even a
half-dead one, which the server notices on its own after 45 seconds without a heartbeat).


WHAT THIS FORK ADDED
--------------------

The base project is Paprikhan's initial commit and its first cleanups (four commits: the
server core, the protocol and level generators, the bundled client files and the plain-text
docs). Everything below was added on top by this fork (git authors "Claw" and "Nico Maas").

Playing the retired 2013 clients:
    - Web login for the standalone "client ver" builds: email + password at client.php plus a
      /register.html page; the Steam login is untouched by it.
    - The January 2013 Chinese build speaks its own January protocol cut: packets it has no
      parser for are dropped, structs are rewritten to their 2013 field lists, error codes are
      renumbered to its era's table, and its revive counter travels as the single int it reads.
    - The April 2013 English build gets its own middle-cut dialect; the web login marks it by
      the "20130416" version field it posts, the Chinese one by the missing field.
    - The 2013 bounty panels actually work: the area-info push that opens the panel, karma
      region unlocks on a clean 2013 map, the client's own mission/area id families translated
      on the wire, slots armed with threat-one ids, and threat buttons that trade karma shards.
    - April sessions play on the era's own content: client-source-2013 tables (level curve,
      items, daily rewards, prices) plus the era's 92 monster definitions in GameData2013,
      with everything the 2013 bundle never shipped falling back to modern content.
    - The January client is served its own era edges: only the fifteen maps its build bundles,
      region unlock and revival prices from the tables bundled in it, the era's 29-wolf
      tutorial waves, and the 5R00 / threat-suffix id families its panel math runs on.
    - Both 2013 builds stand up after a revive: the server carries the innate 12001 shockwave
      state in the skill list, which the 2013 get-up animation chains (April also gets 12002).
    - 2013 daily gift window with its four-track mechanic; April characters stop starting with
      an empty ability bar; monster swings and the death menu speak the April wire; a tool
      switches the Chinese client's game text to the 2014 English tables.

Mail and welcome letters:
    - The mailbox with attachments, the Kickstarter Exclusives and Early Access Starter Pack
      letters at character creation, and the whole rare catalog as one welcome letter.
    - "Accept all" claims what still fits and leaves the rest on the letter; attachments carry
      the client's icon indexes.

Shop and economy:
    - Goto's rare shelf sells named uniques from the original catalog, plus four bundle tiles
      (whole-catalog packs delivered by mail) that the host can switch on or off.
    - Crystal prices are waived era-wide; Transmute pays; the 2013 scroll model becomes an
      account charge counter that follows buy, loot, mail and spending across all six shop
      packs; eras without crafting roll only materials their client can show.

Combat, world and missions:
    - The revive shockwave: the server answers the client's get-up slam and shields the risen
      hunter, and the shield comes off on time, on map change and on the next revive.
    - The old world stops fighting the moment EnterScene arrives; the AI tick mirrors the
      client's regeneration and takes expired food buffs off.
    - Basic attacks are rate-limited (one swing in flight); missions open only where the chain
      unlocked them; packets before a character are guarded; the friend-reward screen gets its
      answer even with nobody to claim from; Act 8 stays dark (the client ships no data for it).
    - Hireable companion allies (Kenshin, Benkei, Koga for 30 minutes) - complete server side,
      though no shipped client build runs that window (see KNOWN-ISSUES).
    - Spirit levels live in the shared counter and the rental keeps the line id the client has
      a model for.

Accounts, connections and hosting:
    - One live session per account: a reconnect hands over half-dead connections, and idle
      ones die after 45 seconds without the client's heartbeat.
    - A flood guard disconnects connections that send more than their share; an optional
      login allow list decides who may play; autosave keeps a crash to minutes and the
      disconnect log says what killed a session.
    - Version matrix with archive.org downloads for all three retired builds, the optional
      bigger Mono game heap that takes the stuttering and minute-long map loads away
      (HOSTING.txt), and KNOWN-ISSUES entries verified live (elder shop tabs, the Goto click
      stub, the stubbed summon window).

Tests: nearly every change above ships with in-process tests; the suite now runs over 900 in
a few seconds.


LAYOUT
------

src/Akaneiro.Server
    The server: stand-in for the game's old login service (HTTP, port 80) and game server
    (TCP, port 7001).
src/Akaneiro.Server/Protocol/Generated
    Server side of the protocol, generated by ProtoGen. Do not edit by hand.
src/Akaneiro.Server/GameData
    Level data (spawn points, spawners, triggers, objects, monsters, missions), generated by
    SceneExtract.
client-source
    Unchanged copies of the client files the server needs: its protocol code and data tables
    (see client-source/README.txt).
client-source-2013
    The April 2013 build's own data tables; April-dialect sessions play on them when the
    folder is present (see HOSTING.txt).
src/Akaneiro.Server/GameData2013
    2013-era server-side content (the era's monster definitions, threat prices); tables the
    era never shipped fall back to GameData.
tools/ProtoGen
    Generates the server protocol code from the client's protocol files.
tools/SceneExtract
    Reads Unity 4 scene and prefab files without Unity and exports the level data.
tools/regen-gamedata-2013.sh
tools/README-2013-extract.md
    Rebuild the GameData2013 files from the recovered April 2013 client.
tools/zhclient-english-tables
    Writes 2014 English game text into a copy of the 2013 Chinese client's table asset.
tools/TestClient
    Smoke test of a running server over the real network path.
tests/Akaneiro.Server.Tests
    In-process game logic tests (xUnit).
data
    Save data, created at runtime (one JSON file per account; not part of the repository).

The client's protocol files (Packet.cs, TypeDefine*.cs, Protocol*_Client.cs) are compiled
directly into the server, so client and server always share one definition. The client's data
tables (items, abilities, levels, prices, ...) are read when the server starts. Both come from
client-source, so the repository is all the server needs.


QUICK START
-----------

Requirements: Windows, the .NET 8 SDK and Akaneiro on Steam. Details and troubleshooting:
HOSTING.txt.

1. Add this line to C:\Windows\System32\drivers\etc\hosts (needs administrator rights):

       127.0.0.1 mw.angry-red.com

2. Start the server from this folder:

       dotnet run --project src/Akaneiro.Server -c Release

3. Start Steam, then start Akaneiro from Steam.


TESTS
-----

    dotnet test tests/Akaneiro.Server.Tests

Runs the game logic in-process (over 900 tests in a few seconds): a TestPlayer talks to a server
session with the client's own protocol code, and a manual clock makes monster fights, cooldowns
and delayed level events run without waiting. The level data tests check every mission on every
threat level for reachable monsters.

    dotnet run --project tools/TestClient -- 8081

A one-second smoke test of a running server over the real network path (web login, TCP,
heartbeat, entering a scene), for example against a second instance started with
--Akaneiro:HttpPort=8081 --Akaneiro:GamePort=7101 --Akaneiro:DataDir=<other folder>


REGENERATING GENERATED FILES
----------------------------

    dotnet run --project tools/ProtoGen -- client-source/Scripts/CS/Protocol src/Akaneiro.Server/Protocol/Generated
    sh tools/regen-gamedata.sh

regen-gamedata.sh rebuilds every file in src/Akaneiro.Server/GameData from the scenes and prefabs
of the full client project, which it expects next to this folder as ../akaneiro-client
(https://github.com/SpicyHorseLegacy/akaneiro-client, about 900 MB). Only this step needs it.


ABOUT
-----

This is an unofficial community project, not affiliated with or endorsed by the makers or
publishers of Akaneiro: Demon Hunters. It contains no game assets; you need your own copy of the
game on Steam. The files in client-source are copied from the client source, and the level data
in src/Akaneiro.Server/GameData is extracted from it.
