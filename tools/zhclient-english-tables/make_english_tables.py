# English game text for the 2013 Chinese client (data only, the .exe stays untouched)
#
# The 2013 "Akaneiro Demon Hunters (ZH)" build keeps every game table (item names,
# quest dialogue, stats, ...) as plain TextAssets in Akaneiro_Data/resources.assets.
# The 2014 web client ships the same tables in EN/RU/ZH. This script writes a copy of
# the 2013 resources.assets whose table contents come from the 2014 EN client, keeping
# the 2013 header lines and column counts (the 2013 loaders parse positionally and
# choke on newer columns) and keeping Chinese rows for ids the 2014 table lost.
#
#   pip install unitypy
#   python make_english_tables.py <zh-client>/Akaneiro_Data <2014-client>/Akaneiro_Data
#
# Writes <zh-client>/Akaneiro_Data/resources.english.assets.
# To use it: back up resources.assets, copy resources.english.assets over it.
# Tables the 2013 parsers cannot take from 2014 data (floats where ints are wanted,
# reshaped create-character info) stay Chinese; UI chrome text is compiled into the
# client and never comes from these tables at all.

import os
import shutil
import sys

import UnityPy

# tables whose 2014 content the 2013 parsers reject outright (verified against the
# 2013 client's loaders: FormatException / NullReference at startup)
KEEP_ZH = {
    "Ability.Ability", "Ability.Object", "Ability.Status", "EquipMastery.level",
    "CreateCharaInfo.Description", "AccessoriesAttribute.Stat", "WeaponAttribute.weapon",
    "MissionDescription.LoadingComics", "MissionDescription.Description", "GameConfig",
}


def textassets(path):
    out = {}
    for obj in UnityPy.load(path).objects:
        if obj.type.name == "TextAsset":
            d = obj.read()
            out[d.m_Name] = d.m_Script
    return out


def rows_of(text):
    return [l for l in text.splitlines() if l.strip() and not l.lstrip().startswith("#")]


def is_int(x):
    try:
        int(x)
        return True
    except ValueError:
        return False


def cells(line):
    c = [x.lstrip("\ufeff") for x in line.split("\t")]
    while c and c[-1] == "":
        c.pop()
    return c


def align(ztypes, etypes):
    """Column map: which EN column feeds each ZH column, or None. Extra EN columns
    are dropped; only appends (or same-type insertions) align."""
    keep, i = [], 0
    for j, t in enumerate(etypes):
        if i < len(ztypes) and t == ztypes[i]:
            keep.append(j)
            i += 1
    return keep if i == len(ztypes) else None


def merge(zh_text, en_text):
    zrows, erows = rows_of(zh_text), rows_of(en_text)
    zhdr_n = next((i for i, r in enumerate(zrows) if is_int(r.split("\t")[0])), len(zrows))
    ehdr_n = next((i for i, r in enumerate(erows) if is_int(r.split("\t")[0])), len(erows))
    zh_hdr, zh_data, en_data = zrows[:zhdr_n], zrows[zhdr_n:], erows[ehdr_n:]
    zn = len(cells(zh_data[0])) if zh_data else 0
    en_n = max((len(cells(r)) for r in en_data), default=0)
    if zn == en_n:
        body = zh_hdr + en_data
    elif zhdr_n >= 1 and ehdr_n >= 1:
        keep = align(cells(zh_hdr[0]), cells(erows[0]))  # the type line is the first row
        if keep is None or len(keep) != zn:
            return None
        body = zh_hdr + [
            "\t".join(r.split("\t")[c] for c in keep) if len(r.split("\t")) > max(keep) else r
            for r in en_data
        ]
    else:
        return None
    en_ids = {r.split("\t")[0] for r in en_data}
    body += [r for r in zh_data if r.split("\t")[0] not in en_ids]
    out = "\n".join(body)
    if zh_text.startswith("\ufeff") and not out.startswith("\ufeff"):
        out = "\ufeff" + out
    return out


def main(zh_dir, en_dir):
    zh_assets = os.path.join(zh_dir, "resources.assets")
    en_assets = os.path.join(en_dir, "resources.assets")
    backup = zh_assets + ".zhbak"
    if not os.path.exists(backup):
        shutil.copy(zh_assets, backup)
    zh, en = textassets(backup), textassets(en_assets)

    outdir = os.path.dirname(zh_assets)
    env = UnityPy.load(backup)
    swapped, kept = [], []
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        d = obj.read()
        name = d.m_Name
        if name in KEEP_ZH or name + ".EN" not in en:
            kept.append(name)
            continue
        merged = merge(zh[name], en[name + ".EN"])
        if merged is None:
            kept.append(name)
            continue
        d.m_Script = merged
        d.save()
        swapped.append(name)
    blob = env.file.save()  # bytes; no temp file, no open handles
    target = os.path.join(outdir, "resources.english.assets")
    with open(target, "wb") as fh:
        fh.write(blob)
    print(f"english tables: {len(swapped)} swapped, {len(kept)} kept chinese")
    print(f"wrote {target}")
    print(f"original chinese kept at {backup}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit("usage: make_english_tables.py <zh-client>/Akaneiro_Data <2014-client>/Akaneiro_Data")
    main(sys.argv[1], sys.argv[2])
