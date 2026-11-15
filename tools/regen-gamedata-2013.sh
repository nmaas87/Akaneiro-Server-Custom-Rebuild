#!/bin/sh
# Regenerates the April-2013 era GameData (src/Akaneiro.Server/GameData2013) from the recovered
# April 16 2013 English fat client (the "win04162" install). Give the client's Win folder as the
# first argument (default ../Akaneiro_win04162_EN_2013/Win). Run from the akaneiro-server folder:
#   sh tools/regen-gamedata-2013.sh [path/to/Win]
# See tools/README-2013-extract.md for what is extracted and for the documented deviations from
# the 2014 GameData shapes.
set -e
WIN=${1:-../Akaneiro_win04162_EN_2013/Win}
OUT=src/Akaneiro.Server/GameData2013
if [ ! -d "$WIN/assetbundles" ]; then
    echo "no assetbundles folder under $WIN - point this script at the April client's Win folder" >&2
    exit 1
fi
mkdir -p $OUT

dotnet build tools/SceneExtract >/dev/null
X="dotnet run --no-build --project tools/SceneExtract --"

# Only monsters.json ships. The other extractions verified that the 2013 client carries no
# monster/breakable PLACEMENT and no mission prefabs at all (that was server-side in 2013 and
# is lost), and its scenes lack the NPC ids and collect targets the server drives - so the
# server keeps the 2014 layout files (same map lineage, coordinates verified identical) and
# GameData's fallback reads them for April sessions too. Writing empty 2013 spawners/triggers/
# scenes into GameData2013 would silence every map; they stay as survey output below.
$X apr-monsters   $WIN $OUT/monsters.json

# survey output (not loaded by the server; see tools/README-2013-extract.md):
SURV=${TMPDIR:-${TMP:-/tmp}}/apr-2013-survey; mkdir -p $SURV
$X apr-scenes     $WIN $SURV/scenes.json
$X apr-spawners   $WIN $SURV/spawners.json
$X apr-triggers   $WIN $SURV/triggers.json
$X apr-missions   $WIN $SURV/missions.json
$X apr-breakables $WIN $SURV/breakables.json

# report only (not GameData): per-map monster/breakable TypeId tables and the mainData
# world-map RegionID / mission-id rows:
#   $X apr-relations $WIN
#   $X apr-regions   $WIN
