# Getting started on a new machine

The checklist for putting XLines on a machine: anyone's (§1), and the owner's second machine, with
the same data and journal as the first (§2, §3). The [README's Quick start](../README.md#quick-start)
explains each step at length; this page is the order to do them in.

## 1. Any machine: clone, set up, start, add a key

**You need:** Git, Docker Desktop (running), the .NET 10 SDK, PowerShell (`pwsh` on macOS or
Linux), and a free key from [The Odds API](https://the-odds-api.com/#get-access) (not
odds-api.io, a different provider).

```powershell
git clone -b LineX_Development https://github.com/wNohejl/XLines.git XLines
```

```powershell
cd XLines
```

```powershell
.\scripts\setup.ps1
```

`setup.ps1` writes `.env` (a random database password; gitignored), makes the HTTPS development
certificate, and asks whether to turn ESPN on. The answer defaults to No; ESPN is an optional
extra for box scores and player stats, licensed for personal use only. `-EnableEspn` answers Yes
without asking.

```powershell
docker compose -f docker-compose.yml -f compose/dev.yml up -d --build
```

Open <https://localhost:9443>, go to **Ops → Odds feeds**, paste the key into **The Odds API
key** and press **Check and save**. It costs no credit. Fixtures for the next eight days arrive
within a minute; prices and scores are pulled from **Pull data**, and each pull states its price
first.

**Check it worked:**

```powershell
docker compose ps
```

```powershell
curl.exe -k https://localhost:9443/ready
```

`xlines-postgres` should be healthy, `xlines-web` up, and `/ready` should answer `Healthy`.

**If something stops you:**

| If | Then |
|---|---|
| A script "cannot be loaded because running scripts is disabled" | Bypass the policy for that run only: `powershell -ExecutionPolicy Bypass -File .\scripts\setup.ps1` |
| `dotnet run` fails with `0x800711C7 … Application Control policy has blocked this file` | Smart App Control is on. Keep to the Docker desk above; turning Smart App Control off is the machine owner's decision and cannot be undone without reinstalling Windows |
| The browser warns about the certificate | `dotnet dev-certs https --trust`, if you want it trusted; it changes the machine's trust store |
| The board is empty | No key yet, or the key was refused: **Ops → Odds feeds** says which |

## 2. The owner's second machine: the same data and journal

The database and the journal travel through the owner's **private** repository
`wNohejl/lineops-journal`, cloned at `data/personal` and never committed to this one (ESPN's and
MLB's data may not be redistributed). It holds two things:

| File | What it is | Made by | Read by |
|---|---|---|---|
| `snapshots/lineops.dump` and `.json` | The database: games, stats, closing lines, odds (custom-format `pg_dump`, a few MB) and a manifest of what it holds | `publish-data.ps1` | `restore-data.ps1` |
| `journal.json` | Your journal entries, parlays, deletions and operator settings, matched to games by provider id | `publish-journal.ps1` | `restore-journal.ps1` |

They are kept apart because a data restore replaces the whole database, and the journal is
merged into it afterwards.

### On the machine that has the newest data: publish

```powershell
.\scripts\publish-data.ps1 -Commit
```

This dumps the database into `data/personal/snapshots/`, leaving out the journal, parlays and
settings, then commits and pushes it in the private clone only. It refuses if `data/personal` is
not a clone of its own.

```powershell
.\scripts\publish-journal.ps1
```

```powershell
git -C data/personal commit -am "journal: export"
```

```powershell
git -C data/personal push
```

### On the other machine: restore

The first time, do §1 with `.\scripts\setup.ps1 -EnableEspn`, then clone the private repository
into the checkout before the first restore:

```powershell
git clone https://github.com/wNohejl/lineops-journal.git data/personal
```

Every time after that, pull instead:

```powershell
git -C data/personal pull
```

Then restore the data first and the journal second, always in this order. A data restore drops
the whole schema, journal included, so a journal merged before it is erased:

```powershell
.\scripts\restore-data.ps1 -Force
```

```powershell
.\scripts\restore-journal.ps1 -DryRun
```

```powershell
.\scripts\restore-journal.ps1
```

`-Force` is needed only when the database already holds games; a first restore does without it.
The dry run reports what the merge would add, change and delete; read it, then run the merge.
The merge is idempotent: a second run reports everything unchanged.

**Not carried over, by design:** the odds key saved in Ops (enter it again on each machine; the
same key may be used on both, and credit pacing reads the provider's own count), sign-in
cookies, and the desk's saved layouts and themes, which live in each browser.

**When to publish again:** whenever the other machine should see the change. That means after a
backfill, after a season closes, or after a stretch of journal entries. Keep journal entries to
one machine at a time where you can. If both machines changed the journal, save this machine's
journal before the data restore erases it, then merge both into the restored database:

```powershell
New-Item -ItemType Directory -Force .\lineops-journal | Out-Null; .\scripts\publish-journal.ps1 -OutFile .\lineops-journal\journal-here.json
```

```powershell
.\scripts\restore-data.ps1 -Force; .\scripts\restore-journal.ps1
```

```powershell
.\scripts\restore-journal.ps1 -InFile .\lineops-journal\journal-here.json
```

Then publish from this machine, so the other one gets the merged journal. `./lineops-journal/`
is gitignored here.

## 3. A checkout made before the rename (LineOps, before 1.0)

On a machine that already ran the desk as LineOps:

1. **Clone again** from `https://github.com/wNohejl/XLines.git` into `XLines`, as in §1, and move
   the private clone across: `data/personal` is a repository of its own and can be moved as a
   folder. Do not push from the old checkout: its `origin` is the old repository.
2. **Copy the machine's settings across:** `.env`, and `src/*/appsettings.Local.json` from
   `src/LineOps.Web` and `src/LineOps.Worker` to `src/XLines.Web` and `src/XLines.Worker`. They
   are gitignored, so a clone does not bring them. If `.env` sets `LINEOPS_TAILNET_HOST`, rename
   it `XLINES_TAILNET_HOST`.
3. **Recreate the containers once** under their new names. The compose project and its volumes
   keep the name `lineops`, so the data is the same database:

   ```powershell
   docker compose -f docker-compose.yml -f compose/dev.yml up -d --build
   ```

   Until this runs, the data scripts cannot find `xlines-postgres`, because the running
   container is still `lineops-postgres`.
4. **Visual Studio:** open `XLines.slnx`, not the old solution.

Nothing else moves: the database, the saved key in `%LOCALAPPDATA%\LineOps`, the user-secrets
and the browser's layouts all keep their old names on purpose
([ADR 0031](adr/0031-xlines-the-name-its-own-repository-and-where-1-0-runs.md), amended).
