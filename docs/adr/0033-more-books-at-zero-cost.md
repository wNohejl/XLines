# ADR 0033 — More books at zero cost

**Status:** Accepted, 2026-11-26. The SportsGameOdds adapter has not been run against the live service (Consequences).

**Builds on:** [ADR 0011](0011-espn-is-the-stats-port-odds-come-from-a-book-market.md) (prices come
from a book market), [ADR 0014](0014-a-real-feed-a-credit-budget-and-what-counts-as-a-game.md) (the
credit budget), [ADR 0024](0024-data-tiered-by-licence.md) (sources tiered by licence) and
[ADR 0026](0026-the-odds-api-is-the-default-source.md) (The Odds API is the default source, and every
metered press states its price first).

## Context

The owner asked how to get more book prices for free, naming bet365, Fanatics, Bally's, FanDuel,
DraftKings and Caesars. The desk names ten books for The Odds API (free Starter plan, 500 credits a
month). Up to ten named books bill as one region unit, so a league pull costs one credit per market:
3 with totals, 2 without. These were the providers' public pages on 2026-11-26:

- **The Odds API's bookmaker list** (<https://the-odds-api.com/sports-odds-data/bookmaker-apis.html>):
  - `ballybet` (Bally Bet, region us2) carries no restriction.
  - `williamhill_us` (Caesars) and `fanatics` are "Only available on paid subscriptions".
  - The same is said of `courtside`, `rebet`, `bet365_au`, `dabble_au` and `bet99_ca_on`.
  - `bet365_au` is also "limited to h2h, spreads and totals for AFL and NRL". The provider lists no US
    bet365 at all.
  - Pinnacle is filed under `eu` only.
- **The Odds API's plans** (<https://the-odds-api.com/#get-access>):
  - Starter is free, 500 credits, "Most bookmakers".
  - The cheapest paid plan is 20K: $30 a month for 20,000 credits, "All bookmakers".
- **SportsGameOdds** (<https://sportsgameodds.com/pricing/>, docs <https://sportsgameodds.com/docs/>,
  terms <https://sportsgameodds.com/terms>). The free plan is called "Amateur" and its tagline is
  "For testing, prototyping, and initial development".
  - It allows 2,500 objects in a rolling 30 days, 10 requests a minute, and updates every 10 minutes.
  - It covers the NFL, NBA, MLB and NHL, among others. UFC is not listed for any plan.
  - Its books are FanDuel, DraftKings, BetMGM, **Caesars**, ESPN BET, Bovada, Unibet, PointsBet and
    William Hill.
  - An object is "the top-level items returned in a request", so an event is one object however many
    books and markets it carries (FAQ).
  - The key goes in the `X-Api-Key` header. Prices are under `GET /v2/events`: `odds` is keyed by
    oddID, and each book's quote is in `byBookmaker.<id>.odds`, with `spread` or `overUnder` beside it.
  - The terms' sentences that bear on this use, summarised:
    - The free tier exists so you can evaluate the service before paying ("so that you can evaluate
      the Service"), and free offerings may be changed or withdrawn.
    - Payment details may be required even on the free plan.
    - Data may not be redistributed through "data dumps, database access, downloadable files, bulk
      exports".
    - When the licence ends, the data is deleted within 30 days, "including from databases, cache, and
      backups".
- **ESPN**'s public odds carry DraftKings only.
- **odds-api.io**, already in the code as `OddsApiIoAdapter` (off by default, its list includes
  `bet365`), is the only route to bet365 the code has. That provider has stopped issuing new free keys,
  so it is not a free route today.

## Decision

**FanDuel and DraftKings are already polled.**

**Bally Bet, by a swap, at the same price.** `ballybet` takes the place of `lowvig` in both hosts'
lists. LowVig.ag is BetOnline's reduced-juice sister book: the least independent of the ten, since
BetOnline is already on the list. The list stays at exactly ten:

pinnacle, draftkings, fanduel, betmgm, betrivers, espnbet, hardrockbet, bovada, betonlineag, ballybet.

**Caesars and Fanatics on The Odds API need the paid plan, and the desk says so.**

- `TheOddsApiBooks.PaidOnly` lists the seven keys the provider marks paid-only.
- When any of them is configured, the feed's status in Ops reads: "Caesars and Fanatics need a paid
  plan at The Odds API; on the free plan they return no prices, and still take a slot of the ten."
  The sentence goes into `OddsProviderState.Reason`, which Ops already draws, and into `Notes`.
- The credit estimate still counts such a book, because it occupies one of the ten slots.

**Past ten books, the desk says what is lost.** It does not bill double.

- The adapter does not send more than ten books by name. It drops the named list and asks for
  `regions=us`, so the price stays one unit (`More_than_ten_books_fall_back_to_one_region_rather_than_billing_two`).
- That loses every named book outside `us`: Pinnacle, theScore Bet, Hard Rock Bet and Bally Bet.
- So a list past ten puts a note in the status naming the books it loses. The status says this before
  anyone presses a pull. `CreditsPerScan` stays the market count, which is what is billed.
- The web host's comment that "an eleventh would double every scan" was wrong against the code, and
  has been corrected.

**Caesars at zero cost comes from a second, independent source: SportsGameOdds, on the owner's own
free key.**

- **Off by default, and the key is the switch.** `Ingestion:SportsGameOdds:ApiKey` turns it on, or
  `SPORTSGAMEODDS_API_KEY` in `.env` for the containers. With no key, nothing is registered.
- **Its own key, row, budget and job.**
  - `SportsGameOddsAdapter` implements `IOddsSource`, so its prices pass through the same
    `OddsIngestionService`, `EntityResolver.ResolveGameAsync` matcher (team names and start time) and
    store-on-change as every book.
  - It is registered as itself, never as an `IOddsSource` the container enumerates, and the registry
    exposes it as `SourceRegistry.SportsGameOdds` rather than in `OddsSources`. Every reader of
    `OddsSources` spends The Odds API's month: `odds:lines:*`, the scheduler's scans and closing scans,
    `LinePollPlanner` and `CreditAllowance`. This source cannot reach any of them.
  - Its job is `sgo:lines:{league}` (NFL, NBA, MLB). Only a press runs it; no scheduler path names it.
- **Its own account.** The source row is `sportsgameodds` (`SourceAccounts.SportsGameOdds`), alone on
  its account, with licence `PersonalUse` (ADR 0024).
  - The row is seeded off and left off. It runs only on request, so it is outside the freshness rules,
    KPIs and alerts that a feed is measured by. Its runs are still recorded against it.
- **The budget is objects, not credits.**
  - `SportsGameOddsBudget` counts this source's `CreditsSpent` over the trailing 30 days against 2,500,
    holding back a reserve of 250. Each run records the objects it spent as its credits.
  - One pull returns at most 50 events, and a pull is cut to what is left above the reserve. It is
    refused when nothing is: `ObjectAllowance.Ceiling` is handed to the adapter as its event ceiling.
  - The menu entry states its price before the press, for example "14 objects · 2,286 left"
    (`IngestionJob.CostUnit = "object"`). The estimate is the desk's games in the next seven days,
    capped at the ceiling, or the ceiling where none are held.
  - The entry asks first when the pull would be cut short.
  - The provider reports no usage on its responses, so the count is this database's alone.
  - The calendar-month budget of 2,500 on the row backs this up through `CreditBudgetGuard`.
- **What a pull asks.** `GET https://api.sportsgameodds.com/v2/events` with these parameters:
  - `leagueID`
  - `bookmakerID=caesars` (configurable through `Ingestion:SportsGameOdds:Bookmakers`)
  - `oddID` set to the six full-game outcomes: `points-home-game-ml-home`, `points-away-game-ml-away`,
    `points-home-game-sp-home`, `points-away-game-sp-away`, `points-all-game-ou-over` and
    `points-all-game-ou-under`
  - `started=false`, `oddsAvailable=true`, `startsBefore` set to now plus seven days, and `limit`
  - `cursor` followed from `nextCursor`, with 6.5 seconds between pages for the 10-a-minute limit
- **The canonical key for Caesars is `williamhill_us`.** That is the key The Odds API files Caesars
  under. The adapter maps SportsGameOdds' `caesars` to it, so it is one book on the desk whichever
  source brought it. A paid Odds API key would extend the same row of prices rather than start a
  second "Caesars".
  - `BookNames` already names both keys "Caesars" with the mark CZ.
  - `unibet` maps to `unibet_us` and `pointsbet` to `pointsbetus`. Every other id is kept as sent.
- **Sides by the event.** The board pairs a side with its game by the team's name. The adapter
  implements `IBookSidesByEvent`, so `OddsIngestionService` renames its moneyline and spread sides to
  the matched game's own home and away teams. The Odds API's prices are written as they always were.
- **How it fails.** These all throw `OddsFetchFailedException`, with the objects already returned as
  its cost:
  - a refused key (401), a plan refusal (403) or the rate limit (429), each in plain words
  - an error envelope, or a body that is not JSON
  - a page whose every event is unreadable

  Every page is read before anything is handed back, so a pull is written whole or not at all. The run
  fails, its row says why, and the press's toast says it as "NFL from SportsGameOdds failed: …".

**bet365 for the US has no free, permitted source.** The Odds API's only bet365 is Australian, paid,
and AFL and NRL only. The odds-api.io route needs a key the provider no longer issues free. Nothing is
built on either.

**Scraping the books is refused.** Reading bet365's, Fanatics' or Caesars' own sites breaches their
terms of use. It is also brittle by design: the sites change their pages and block automated clients
to stop exactly this. And it would put prices the desk has no right to keep in the same tables as
licensed ones. None is built.

## The arithmetic

- **The Odds API:**
  - The ten books bill as one unit, so a league pull is 3 credits with totals and 2 without, before the
    swap and after it.
  - An eleventh book would not double the bill. It would drop the named list (above).
  - The paid plan that sells Caesars and Fanatics is $30 a month (20K).
- **SportsGameOdds:**
  - One object per game returned.
  - A week ahead is about 16 NFL games, about 50 NBA and about 90 MLB, the last capped at 50 a pull.
  - The 2,250 usable objects a window come to roughly:
    - 140 NFL pulls
    - 45 NBA pulls
    - one NFL and one NBA pull a day for a month, at about 66 objects a day, or 1,980
  - None of this touches The Odds API's 500 credits.

## How the owner turns it on

1. Sign up for your own free Amateur key at <https://sportsgameodds.com/pricing/>. The terms say
   payment details may be asked for even on the free plan.
2. Put the key in `.env` as `SPORTSGAMEODDS_API_KEY=<key>` for the Docker desk, then
   `docker compose up -d`. For a host run, use
   `dotnet user-secrets set Ingestion:SportsGameOdds:ApiKey <key>` or
   `src/XLines.Web/appsettings.Local.json`. Never put it in a committed file.
3. The pull menu then lists **Pull Caesars — NFL / NBA / MLB** under *Costs credits*, each with its
   object price. Ops → Odds feeds lists SportsGameOdds with its allowance.

## Consequences

- **Bally Bet is priced from the next pull, at no extra cost. LowVig.ag no longer is.** Its past prices
  stay where they are.
- **A misconfigured list is visible before it costs anything.** That covers a paid-only book or an
  eleventh book.
- **Caesars is available at no cost, with conditions the owner should weigh:**
  - SportsGameOdds frames its free plan as evaluation ("testing, prototyping, and initial
    development"). A few presses a week for a personal desk is the light use that fits. The plan may be
    withdrawn at any time.
  - Its data stays `PersonalUse`. If the key is given up, the terms ask for its data to be deleted
    within 30 days, backups included.
  - The data snapshot (`publish-data.ps1`) carries these rows into the owner's private journal
    repository. That is the owner's own copy, and must never be published, as with ESPN's.
- **Unverified against the live service.** No key was held, and no call was made. The tests follow
  the documented field names, not a captured response. Three points are read defensively and are the
  first to check on a real key:
  - The docs disagree on whether a book's entry carries `spread` and `lastUpdatedAt`.
  - Numbers are documented as strings, and numbers are accepted too.
  - Team names: `names.long` is assumed to be the club's full name, and is matched through the desk's
    own table (`TheOddsApiTeams`).

  If the names differ, the matcher's normalised-name and known-as passes are the guard, and a game
  matched to no club is created beside the real one exactly as for any book. The first real pull is
  worth checking against the board.
- **Open:**
  - The Ops window cannot yet take the SportsGameOdds key; for now it is set in configuration or the
    environment.
  - The VM deployment's `deploy/compose.yml` does not pass the key yet.
