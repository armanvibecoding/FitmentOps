# Contributing to FitmentOps

Thank you for improving FitmentOps. Keep changes focused, evidence-backed, and safe for commerce data.

## Development workflow

1. Create a branch from the latest `main` using `feature/`, `fix/`, or `docs/`.
2. Keep one coherent change per pull request.
3. Add or update tests for behavior changes.
4. Run the relevant local gates before pushing.
5. Explain user impact, failure behavior, migrations, configuration, and rollback in the pull request.

## Required checks

Backend changes:

```bash
dotnet restore FitmentOps.sln
dotnet build FitmentOps.sln --configuration Release --no-restore -warnaserror
dotnet format FitmentOps.sln --verify-no-changes --no-restore
dotnet test AutoPartsStore/Backend/AutoPartsStore.API.Tests/AutoPartsStore.API.Tests.csproj --configuration Release --no-build
dotnet test AutoPartsStore/Backend/AutoPartsStore.API.IntegrationTests/AutoPartsStore.API.IntegrationTests.csproj --configuration Release --no-build
```

Frontend changes:

```bash
cd AutoPartsStore/Frontend/client
npm ci
npm run lint
npm test
npm run build
npm audit --audit-level=high
```

Repository safety:

```bash
python scripts/scan_secrets.py
git diff --check
```

## Domain invariants

- The browser is not authoritative for price, stock, ownership, or payment state.
- Unknown fitment evidence must not become a positive compatibility result.
- Provider callbacks must be authenticated, idempotent, and replay-safe.
- Payment and refund transitions must be monotonic and amount-bounded.
- Inventory and migrations must be safe under concurrent requests.
- Missing provider configuration must fail closed.
- Sensitive values must not enter JSON responses, logs, fixtures, or committed files.
- Authorization tests must cover every new administrative endpoint.

## Pull requests

Use a clear title and describe:

- what changed and why;
- affected users and operators;
- tests and measurements;
- schema or configuration changes;
- deployment and rollback steps;
- known limitations.

Do not weaken assertions, skip tests, or increase timeouts solely to make a failing check pass.
