# Fixed solver input

`course2-live-2026-09-23.json` is a byte-for-byte copy of the initialized live
simulation export captured while Course 2 was active. SHA-256:

`5D1B65E6DD110721216D33E1597C5975614ADC2D440A59948FB791BFDC6C68E5`

The snapshot contains geometry for multiple courses. Course 2's ordinary
quick-restart pose, measured from the native result trace, is
`(4783.434, 1866.225)`; the model's captured player pose is not the restart
pose. The earlier Course 3 direct candidate records `(9554.803, 6955.015)`
as its source pose, but this snapshot was taken with Course 2 active. A Course
3 search using these colliders is reproducible offline and remains screening
only until a fresh Course 3 live export and native trace confirm equivalence.

Never overwrite this file with `/api/simulation/export`; that endpoint writes
the mutable game-side `live-simulation-model.json`.
