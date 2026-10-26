# Čitač faktura (invoice reader)

A web app for accounting offices: clients send photos or PDFs of invoices, AI reads them,
the accountant checks and confirms, and the app exports a file for the office's accounting program.

It runs in one of two modes (see [Demo and pilot](#demo-and-pilot)): a **demo** with invented
clients, suppliers and invoices, or a **pilot** for real offices with their own logins.

## What's inside

| Part | Where |
| --- | --- |
| ASP.NET Core 8 Razor Pages app | `src/InvoiceReader.Web` |
| Database: SQLite via EF Core (file in `App_Data/invoices.db`), schema by EF Core migrations, applied on start | `Data/`, `Data/Migrations/`, `Data/DatabaseSetup.cs` |
| Demo or pilot mode | `Services/AppMode.cs` |
| Logins: users per office, hashed passwords, password change, login rate limit; owner's admin page for offices and users | `Services/UserAccounts.cs`, `Pages/Login`, `Pages/Password`, `Pages/Admin` |
| Background reader: AI read → checks → account suggestion | `Services/ProcessingWorker.cs` |
| AI readers: Demo (no key), Claude, Gemini | `Services/Readers.cs` |
| Supplier list from confirmed invoices: fixes a misread ID (one digit off, or matching VAT number) and fills in the confirmed name | `Services/KnownSuppliers.cs` |
| Repeat uploads: a file identical to one already confirmed for the client reuses that data, no AI call | `Services/ProcessingWorker.cs` |
| Uploaded photos made small (max 2000 px, JPEG, turned upright, EXIF/GPS dropped); photos up to 25 MB, PDFs up to 7 MB | `Services/ImageShrinker.cs`, `Services/FileStore.cs` |
| Maths and format checks (net + VAT = total, 17% VAT, 13-digit ID…) | `Services/InvoiceChecks.cs` |
| Generic CSV export | `Services/CsvExporter.cs` |
| Six fictional sample invoices | `SampleInvoices/` |
| Unit tests (xUnit) | `tests/InvoiceReader.Tests` |
| CI (build, test, Docker build) | `.github/workflows/ci.yml` |
| Container, start script, database backup (Litestream) | `Dockerfile`, `deploy/` |
| Render Blueprints: free demo, paid pilot | `render.yaml`, `render-pilot.yaml` |

Screens (UI is in Bosnian): login, clients, client page with upload link, review (invoice left,
fields right), export, settings (chart of accounts, clients), and the client's public upload page `/u/{token}`.

## Run locally

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
cd src/InvoiceReader.Web
dotnet run
```

Open the URL it prints (e.g. http://localhost:5000), log in with `demo@demo.ba` / `demo1234`
(change both in `appsettings.json` → `Demo`).

Run the tests from the repository root:

```bash
dotnet test
```

After changing an entity in `Data/`, add a migration (the test suite fails until you do):

```bash
dotnet tool restore
dotnet ef migrations add NameOfTheChange --project src/InvoiceReader.Web --output-dir Data/Migrations
```

## Demo and pilot

Set `Mode` in `appsettings.json` or as an environment variable.

| | `Mode=Demo` (default) | `Mode=Pilot` |
| --- | --- | --- |
| Data | Invented demo office, clients and invoice history, added on start | Nothing invented, ever |
| Demo login (`Demo:Email` / `Demo:Password`) | Yes | No |
| "Dodaj primjere faktura za demo" button | Yes | No |
| AI | Any, including Demo mode without a key | A real provider with a key, or the app refuses to start |

In both modes, `Admin:Email` and `Admin:Password` (at least 10 characters) create **your own login**
on the first start, in an office of its own. That login sees **Administracija**: create an office with its
first accountant, add more users, reset a password, or switch a user off. New passwords are generated and
shown once, to pass on; users change them under their name in the top bar. A reset or a switched-off
user is logged out everywhere. The admin password from configuration is only used the first time.

Try the pilot locally (any folder for `DataDir`; the AI key must be real for invoices to be read):

```powershell
dotnet run --project src/InvoiceReader.Web -- --Mode Pilot --DataDir C:/temp/pilot --Ai:Provider Claude --Ai:ClaudeApiKey sk-ant-... --Admin:Email you@example.com --Admin:Password "a-long-password"
```

## AI modes

Set `Ai:Provider` in `appsettings.json` or with environment variables (double underscore = section):

| Provider | Cost | Use it for |
| --- | --- | --- |
| `Demo` (default) | free, no key | Demos with the bundled samples. Recognises the six sample files; other files come back empty for manual entry |
| `Claude` | pay per use, about $0.001 per invoice with Haiku | Real reading. Set `Ai__ClaudeApiKey`. Uses `claude-haiku-5-5`, and `claude-sonnet-5-5` for a second read when the checks fail |
| `Gemini` | free tier available | Trying real AI reading for free, **with sample invoices only**: Google may use free-tier data to improve its products. Set `Ai__GeminiApiKey`; check the model name in `Ai:GeminiModel` against Google's current list |

Example (PowerShell):

```powershell
$env:Ai__Provider = "Claude"; $env:Ai__ClaudeApiKey = "sk-ant-..."; dotnet run
```

Never commit API keys. Use environment variables, `dotnet user-secrets`, or the hosting provider's settings.

## Demo scenario (5 minutes)

On a fresh start the app fills in demo history (`Data/DemoInvoices.cs`): **Demo Pekara** has three
confirmed invoices ready for export and one waiting for review, **Test Servis** has three exported
for October 2026 and one waiting. Their accounts are learned office-wide, so the Telekom, fuel and
office-supplies invoices are pre-filled for every client. **Primjer Market** starts empty for the
walkthrough below.

1. Log in. Open **Primjer Market d.o.o.**
2. Click **Dodaj primjere faktura za demo**. Six invoices appear; the AI reads them in a few seconds.
3. Click **Pregledaj**. Invoice on the left, fields on the right.
4. First Energo invoice: new supplier, so pick account `5120 Troškovi električne energije`, then **Potvrdi** (Ctrl+Enter).
5. Second Energo invoice: the account is already filled in, "Prijedlog na osnovu ranijih knjiženja". That's the learning.
6. Servis Klima invoice: the check warns that net + VAT ≠ total (the "AI" misread 234,00 as 243,00). Fix the total, confirm.
7. **Izvoz**: pick the client and October 2026, download the CSV, open it in Excel.
8. Show the client side: copy the upload link from the client page and open it on a phone.

## Deployment

The app deploys as a Docker container. All its data (database, uploads, the keys that encrypt login
cookies) lives in `DataDir`, `/app/data` in the container.

### Free demo

**Render (simplest)**: the repo includes a Blueprint, `render.yaml` (free plan, Frankfurt, health
check, deploys after GitHub CI passes). In Render choose *New > Blueprint*, pick this repo and enter a
`Demo__Password` when asked. Add AI keys later under the service's *Environment*. Limits of the free instance: it sleeps after 15 minutes
without traffic (first request then takes about a minute), and the file system is temporary, so the
database and uploads reset on every restart or redeploy. For a demo that's fine: the app re-creates
the demo data on start. Open the site a minute before showing it.

**Azure App Service Free (F1)**: also runs .NET for free with a daily CPU quota; good if you prefer
staying in the Microsoft ecosystem.

### Pilot with real offices

`render-pilot.yaml` is a second Blueprint (in Render: *New > Blueprint*, this repo, **Blueprint Path**
`render-pilot.yaml`). It is kept out of `render.yaml` on purpose, because it **costs money**: a Starter
instance (about $7/month) with a 1 GB persistent disk (about $0.25/GB/month) mounted at `/app/data`.
Render asks for:

- `Admin__Email`, `Admin__Password`: your login.
- `Ai__ClaudeApiKey`: a paid Anthropic key. Set a monthly spend limit in the Anthropic Console. Haiku
  reads an invoice for a fraction of a cent.
- `LITESTREAM_*` (optional, recommended): off-site database backup, below. Leave `LITESTREAM_BUCKET`
  empty to turn it off.

With a disk, Render stops the old instance before starting the new one, so each deploy has a short
downtime and the service runs as one instance. That's fine for a pilot.

**Database backup (Litestream)**: with `LITESTREAM_BUCKET` set, `deploy/docker-entrypoint.sh` runs the
app under [Litestream](https://litestream.io), which copies every database change to S3-compatible
storage and keeps 7 days of history. On an empty disk it first restores the latest backup. For Cloudflare
R2 (10 GB free): create a bucket and an R2 API token, then set `LITESTREAM_BUCKET`, `LITESTREAM_ENDPOINT`
(`https://<account id>.r2.cloudflarestorage.com`), `LITESTREAM_ACCESS_KEY_ID` and
`LITESTREAM_SECRET_ACCESS_KEY`; `LITESTREAM_REGION` is `auto`. Litestream backs up the database only, not
the uploaded files; for those rely on the host's disk snapshots (check what your Render plan keeps).

The same image runs on any Docker host (e.g. a small Hetzner VPS in Germany behind Caddy for HTTPS):
mount a volume at `/app/data` and pass the same environment variables.

Later, with many offices: PostgreSQL instead of SQLite (change `UseSqlite` to `UseNpgsql` and create a
fresh migration) and private cloud storage for files behind `FileStore`.

## What's deliberately left out

- Self-service sign-up and "forgot password" emails (the owner creates offices and resets passwords on Administracija)
- Resizing photos in the browser before upload (would save mobile data; today the server does it)
- Exporters for specific accounting programs (one class per program, next to `CsvExporter`)
- Bank statements, reminders to clients, billing
