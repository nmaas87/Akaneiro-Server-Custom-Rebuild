# April 2013 GameData extraction (GameData2013)

Re-run with `sh tools/regen-gamedata-2013.sh [path/to/Win]` (default `../Akaneiro_win04162_EN_2013/Win`,
the recovered April 16 2013 English fat client). Only `monsters.json` ships into
`src/Akaneiro.Server/GameData2013/` (the files the server actually loads there, same names as
the 2014 `GameData`); the other extractions land in a survey folder under the system temp dir
as proof of what the client does NOT contain. Loading stays correct through GameData's
fallback: scenes/spawners/triggers/missions/breakables stay the 2014 files, whose map lineage
is identical (spawn coordinates match within a meter) and whose placement is the only playable
data left - the 2013 layout lived on the original server and is gone.

Inputs are the client's own built Unity 4 data: `Win/assetbundles/*.unity3d` + `*.assetbundle`
("UnityWeb" LZMA containers, decoded by `UnityCfsFile`/`LzmaRaw`; the serialized files inside
carry full type trees, so objects decode generically like the 2014 editor assets) and the
type-tree-less player files `Win/Akaneiro_Data/*` (only read by the report commands).

## What each file contains

- **scenes.json** - Spawn = the world position of the player object carrying `PreLoadPlayer`
  (the April scenes have no `CreatePlayerForLD` marker). `Npcs` = `ShopNpc` components of the
  tutorial hub; the April prefabs serialize `npcType` 0 (the enum was not used yet), so `NpcId`
  is 0 and `Name` carries the `npcName` string the 2013 client displayed.
- **spawners.json / triggers.json** - empty per map. Verified: the April map bundles contain
  zero `NpcSpawner`/`Trigger_*` components (the extractor refuses if any appear). The April
  client received monster and trigger placement from the server at runtime
  (`On_MissionListAck`, `On_BreakableActorEnter`); that layout data existed only on the lost
  2013 server and cannot be recovered from the client.
- **missions.json** - empty. No mission-definition prefab (the 2014 `branchArray` marker) exists
  in any April bundle. The April mission identity is the `1RM` id family (111..183, three per
  area 5100..5800 plus 999 village) from the client's own `MissionDescription` table; the area
  unlock table is `MissionArea.Area` (5000 tutorial, 5100..5800 areas).
- **monsters.json** - one entry per monster TypeId listed in the per-map `*Relation.assetbundle`
  `MonsterType` tables, decoded from the named `CH_*` prefab bundles. Where the prefab component's
  own `TypeID` differs from the Relation id, the Relation id wins (the server addressed monsters
  through the Relation table). April `LootDrop` has no `ConsumableDropList` (2014 addition), and
  many monsters genuinely have empty `WakeupAnim`/`SpawnAnims` (zeros, not extraction failures).
- **breakables.json** - per map the set of breakable TYPE definitions from the Relation table's
  `BreakableType` section, decoded from the prop prefab bundles (Woven Basket, Chest, ...).
  There are **no Position/Rotation/Scale fields**: the 2013 client never knew where breakables
  stood (the server announced them); placement was 2013-server-side data and is unrecoverable
  from the client. Integrating these into the server requires choosing placements (or replaying
  the modern layout).

## Report commands (not GameData)

- `apr-relations <win>` - the per-map MonsterType/BreakableType TypeId tables.
- `apr-regions <winDir>` - decodes the type-tree-less `_UI_CS_WorldRegion` / `_UI_CS_RegionLevel`
  / `_UI_CS_WorldBigRegion` MonoBehaviours in `mainData` (hardcoded layouts from the client's
  Assembly-CSharp, accepted only on exact end-of-object match) and prints the world-map RegionIDs
  and the per-area mission id rows. Findings: 8 regions with RegionID 5100..5800 (the tutorial
  area 5000 and testbox 6000 have no world-map region), and the `_UI_CS_RegionLevel` MissionID
  arrays serialize as zeros - `MissionMaster.MissionLevelToID` arms them at runtime from the
  server's mission list, matching each map's THREAT-1 id (5111, not the 2014 client's 5110 map
  base); the table rows (111..183 plus 999) only feed names and descriptions.
