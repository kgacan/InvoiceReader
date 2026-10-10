# Čitač faktura (invoice reader) — demo

A demo web app for accounting offices: clients send photos or PDFs of invoices, AI reads them,
the accountant checks and confirms, and the app exports a file for the office's accounting program.

All clients, suppliers and invoices in this demo are invented.

## What's inside

| Part | Where |
| --- | --- |
| ASP.NET Core 8 Razor Pages app | `src/InvoiceReader.Web` |
| Database: SQLite via EF Core (file in `App_Data/invoices.db`) | `Data/` |
| Background reader: AI read → checks → account suggestion | `Services/ProcessingWorker.cs` |
| AI readers: Demo (no key), Claude, Gemini | `Services/Readers.cs` |
| Maths and format checks (net + VAT = total, 17% VAT, 13-digit ID…) | `Services/InvoiceChecks.cs` |
| Generic CSV export | `Services/CsvExporter.cs` |
| Six fictional sample invoices | `SampleInvoices/` |
| Unit tests (xUnit) | `tests/InvoiceReader.Tests` |
| CI (build, test, Docker build) | `.github/workflows/ci.yml` |

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

1. Log in. Open **Primjer Market d.o.o.**
2. Click **Dodaj primjere faktura za demo**. Six invoices appear; the AI reads them in a few seconds.
3. Click **Pregledaj**. Invoice on the left, fields on the right.
4. First Energo invoice: new supplier, so pick account `5120 Troškovi električne energije`, then **Potvrdi** (Ctrl+Enter).
5. Second Energo invoice: the account is already filled in, "Prijedlog na osnovu ranijih knjiženja". That's the learning.
6. Servis Klima invoice: the check warns that net + VAT ≠ total (the "AI" misread 234,00 as 243,00). Fix the total, confirm.
7. **Izvoz**: pick the client and October 2026, download the CSV, open it in Excel.
8. Show the client side: copy the upload link from the client page and open it on a phone.

## Free deployment

The demo deploys as a Docker container. Two free options:

**Render (simplest)**: push this repo to GitHub, then in Render create a *Web Service* from the repo
(it finds the `Dockerfile`), pick the Free instance type, and add environment variables
(`Demo__Password`, and AI keys if used). Limits of the free instance: it sleeps after 15 minutes
without traffic (first request then takes about a minute), and the file system is temporary, so the
database and uploads reset on every restart or redeploy. For a demo that's fine: the app re-creates
the demo data on start. Open the site a minute before showing it.

**Azure App Service Free (F1)**: also runs .NET for free with a daily CPU quota; good if you prefer
staying in the Microsoft ecosystem.

Before real customers: move to a paid plan with persistent storage, switch SQLite to PostgreSQL
(e.g. Supabase in Frankfurt; change `UseSqlite` to `UseNpgsql`), add EF Core migrations instead of
`EnsureCreated`, real user accounts, and private cloud storage for files.

## What's deliberately left out of the demo

- Real user accounts and multiple offices (the data model already has `OfficeId` everywhere)
- Image resizing before AI (the AI APIs downscale large images themselves)
- Exporters for specific accounting programs (one class per program, next to `CsvExporter`)
- Bank statements, reminders to clients, billing
