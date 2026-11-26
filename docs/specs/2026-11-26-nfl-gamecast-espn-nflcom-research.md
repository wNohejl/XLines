# The NFL gamecast against ESPN's and NFL.com's — research

**Date:** 26 November 2026 (NFL week 12, Thanksgiving; three games today).
**Follows:** `2026-10-24-live-gamecast-research.md` (the feed, the play store and the window, all
shipped) and `docs/plans/2026-11-01-live-gamecast.md`.
**Method:** ESPN's Gamecast page and `summary` document for CIN @ WSH (event `401873078`, a
final); NFL.com's Game Center for the same game (`/games/bengals-at-commanders-2026-reg-11`),
its tabs and the requests behind them; a read of the Gamecast window's code and of what the
play store holds (80,000 NFL plays, 9,600 drives).

**Question asked:** the Gamecast window does not read like a gamecast. What do ESPN's and
NFL.com's show that ours does not, and what of it can be drawn from what we already hold?

---

## 1. What each page is made of

### ESPN Gamecast (`espn.com/nfl/game/_/gameId/{id}`)

| Block | Live | Final | Source in `summary` |
|---|---|---|---|
| Header: crests, records, score, linescore | yes | yes | `header` |
| Field: the drive in progress drawn on a field — one arc per play, pass arcs high and run arcs flat, the ball, the line to gain, end zones in the clubs' colours | yes | no (replaced by video) | `drives.current` |
| Play-by-play: **Scoring Plays / All Plays**, drives as accordions with "15 plays, 71 yards, 7:57" and the score they left | yes | yes | `drives.previous[]`, `scoringPlays[]` |
| Game leaders: passing, rushing and receiving yards, sacks, tackles, per side | yes | yes | `leaders[]` (the box score's maxima) |
| Probabilities: **Win / Cover** | yes | yes | `winprobability[]`, core `probabilities` |
| Team stats: total yards, turnovers, first downs, penalties, 3rd and 4th down, red zone, possession | yes | yes | `boxscore.teams[].statistics` |
| Game odds, game information (venue, attendance, officials) | yes | yes | `pickcenter`, `gameInfo` |

A play in `summary` carries `start`/`end` (down, distance, `yardLine`, `yardsToEndzone`),
`statYardage`, `type`, `yardsAfterCatch` on a reception, and nothing that places a player on the
field: ESPN's field is drawn from the same yard lines we store. There are no coordinates to want.

### NFL.com Game Center (`nfl.com/games/{away}-at-{home}-{year}-reg-{week}`)

Tabs **Recap / Stats / Highlights & Replays**. Recap is **Scoring Drives / All Drives**: a card
per drive under a heading per quarter — the result as its title ("Field Goal", "Punt", "Downs"),
then PLAYS · YDS · TTL TIME and the score. Open, each play has a *headline* over its text:
"6 Yard Pass", "-6 Yard Sack", "Pass Incomplete", "Punt", then "3rd & 16 at CIN 18" and the
clock. The headline is what makes a drive scannable; ESPN's rows and ours lead with the raw
play-by-play sentence.

**As a data source: no.** Every number on the page comes from `api.nfl.com`
(`/experience/v2/gamedetails/{id}`, `/football/v2/…`), which answers `401 Unauthorized` without
a bearer token the site mints for its own client. Taking that token is working around an access
control, on a feed the league licenses; it is not a port this project builds. NFL.com is a
second *reading* of the same game — how a league lays its own play-by-play out — and that is
how it is used here. ESPN stays the one stats source (ADR 0011).

## 2. What the Gamecast window does today

Title with clock and situation; then cards: **Field** (only while the game is live), **Score by
quarter**, **Team comparison**; then tabs: **Plays** (drives as disclosures, with a `FieldSpan`
strip each), **Scoring**, **Win probability** (with cover and over), **Box score**.

The gaps, in the order a reader meets them:

1. **A finished game has no field.** The field card is `@if (isLive)`. A game is live three
   hours a week and final the rest of it; almost every time the window is opened it is on one of
   the 440 finals in the store, and the one drawing that makes it a gamecast is not there.
2. **The field draws a drive as stacked bars.** Lanes of rectangles read as a chart of the
   drive, not as the ball moving down a field. ESPN's arcs — a pass thrown high, a run kept
   low, each landing where the next begins — read as football at a glance.
3. **Pressing a drive does not move the field.** The log and the field do not know each other.
4. **No drive chart.** Neither the whole game on one field nor any way to see which drive went
   where without opening twenty disclosures.
5. **A play row leads with the sentence.** "(Shotgun) J.Daniels pass short right to A.Williams
   to WAS 41 for 16 yards (J.Battle)." has no headline to scan by.
6. **No game leaders.** The box score holds them; nothing says who they are without opening it.

Not gaps: the win and cover probabilities, the team comparison, the scoring summary and the box
score are there and match ESPN's. Odds are the desk's own business (the Board, Line movement).
Venue and officials are the Game window's.

## 3. What is built

All from the play store and the box score as they are: no new request, no schema change, no
ingestion change.

1. **The field is drawn for any drive, live or final** (`FieldGraphic`): each play an arc from
   its start to its end — a pass high, a run low, a loss arcing back, a penalty dashed, the
   scoring or turnover play marked — in the colour of the side with the ball, with the drive's
   start marked and its result named. While live it is the drive in progress, as now, with the
   ball, the scrimmage line and the line to gain.
2. **A drive chart** (`DriveChart`): every drive of the game as one bar on a shared field frame,
   a row each, coloured by side, its result at the end; pressing one selects it.
3. **Selection joins them**: the drive opened in the log, or pressed on the chart, is the drive
   on the field. Live, the field follows the current drive until one is chosen, and a key
   returns to it. Final, it opens on the last scoring drive.
4. **A play has a headline** (`PlayHeadline`): "16-yard pass", "6-yard sack", "Incomplete
   pass", "Punt", "Touchdown, 31-yard pass", from the play's type and yardage; the sentence
   stays beneath it.
5. **Game leaders** (`GameLeaders`): passing, rushing and receiving, a row per category with
   each side's leader, from the box score already read.

## 4. Verification

- A final (CIN @ WSH, game 2049): the field shows a drive, the chart shows twenty, pressing a
  drive moves the field, the leaders read Burrow 300 / Daniels 289.
- Live, today (CHI @ DET 13:00 ET): the field follows the current drive and the ball moves.
- `dotnet build XLines.slnx` clean; `dotnet test XLines.slnx` green.
