games.txt maps a game's executable name to the name people know it by, so a run of `Cyberpunk2077.exe` is
labelled "Cyberpunk 2077". It is derived from CapFrameX's `Processes.json` (commit 9f488ea6), MIT licence in
LICENSE beside it: only entries with a display name, none of the entries CapFrameX marks as non-games, case
duplicates folded. It ships inside Ohman.exe as a resource. Nothing is fetched or sent anywhere at run time.
