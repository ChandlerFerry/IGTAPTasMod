# TAS text

A route's inputs as text (`TasMarkup.cs`), for editing by hand, diffing and sharing: the studio's TAS tab (**Text**),
`GET/POST /api/tas/text`, the `.tas` download, and share import (paste the text). One line per input run (`#` comments, `@ note` a marker on the next line's first input tick); lines are
raw-input steps one to one, so text and route convert both ways without loss.

```
# a comment
@ first wall jump
   3
# 12 ticks: stick right, jump held (pressed on the first tick, where the hold starts)
  12,R,J
# part tilt 0.55, the jump still held
   5,R0.55,J
# stick left and up, dash; in its freeze the stick turns to (0.35, 1)
   1,L,U,X(0.35:1)
# a tap: press and release in one tick
   1,P,C
```

Ticks are input ticks (dash and spring ticks take none, `docs/human-input.md`). After the count, any of:

| Token | Input |
|---|---|
| `R`, `L`, `R0.55`, `L0.3` | stick x: full right/left, or a part tilt (0.3..0.8) |
| `U`, `D` | stick y up / down |
| `J` | jump held; where a hold starts (the line before has no `J`/`H`) it also presses |
| `H` | jump held without that press (a hold carried in) |
| `P` | jump press where `J` wouldn't press (no hold, or again while held) |
| `C` | jump release (cut) on the run's first tick |
| `X`, `X+3`, `X(0.35:1)`, `X+3(:-1)` | dash press; `+n` jump on its nth dash frame; `(x:y)` the stick in its freeze |
| `S3` | dash on a spring's 3rd tick |
| `K(x:y)` | the stick held through a dash or spring lock (blank: unset) |
| `Z12`, `Z12@0.3` | open the pause menu for 12 frames (after tick 0's frame 3) |
| `Q` | quick restart |

A press, release, dash, lock, pause or restart acts on the run's first tick only. Stick values outside what a controller
gives are snapped to it when the text is applied (README `### Rules` 2).
