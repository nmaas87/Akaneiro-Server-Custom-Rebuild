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
        Barely tested. The server speaks its January 2013 protocol cut (dropping messages it
        has no parser for, rewriting structs to its older field lists, and renumbering error
        codes), and login plus core gameplay work at packet level, but this client is served
        from the 2014 content tables and nobody has played it end to end - expect rough edges.
        Verified against an untouched build: even though the Chinese service shipped this client
        as late as August 2014, its protocol classes never picked up the April 2013 or 2014
        additions - it needs its own dialect, not the April 2013 one. Its bounty panel speaks
        its own id families: the mission list carries threat-suffixed ids (5xy1) that arm its
        slots, the region push carries whole-region ids (5R00) that light its map icons, and
        unlocking a region charges the price ladder of its bundled MissionArea.Area table, and
        its death menu revives on the ladder of its bundled RevivalCost.Costs table. Its
        mission map is limited
        to the fifteen maps that build bundles (Act 1-5, three per region): the fourth-mission
        maps and Acts 6-8 stay dark, so it never offers a level its client cannot load. Like the
        April build it opens its tutorial doors by wolf-kill milestones, so its tutorial spawns
        the era's 29-wolf waves instead of the 2014 layout's 17 (which can never finish).

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
tools/ProtoGen
    Generates the server protocol code from the client's protocol files.
tools/SceneExtract
    Reads Unity 4 scene and prefab files without Unity and exports the level data.
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
