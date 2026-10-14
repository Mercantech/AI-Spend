# AI-Spend

Platform til historisk AI-forbrug: Cursor (events med input/output/cache-tokens) og OpenAI (usage + costs). Org-klar datamodel med Mercantec Auth.

## Stack

- **api** — .NET 10, EF Core, Postgres
- **web** — Vite + React, nginx proxy `/api` → `aispend-api`
- **Auth** — Mercantec Auth (OIDC + PKCE)

## Lokal kørsel

```powershell
copy .env.example .env
# ret POSTGRES_PASSWORD + CREDENTIAL_ENCRYPTION_KEY
docker compose -f docker-compose.yml -f docker-compose.local.yml up --build
```

- Web: http://localhost:5193  
- API: http://localhost:8089/api/health  

Frontend-dev uden Docker:

```powershell
cd web
npm install
npm run dev
```

API lokalt (kræver Postgres):

```powershell
cd api
$env:CONNECTION_STRING = "Host=localhost;Port=5434;Database=aispend;Username=aispend;Password=..."
dotnet run
```

## Env

Se `.env.example`. Vigtige nøgler:

| Variabel | Formål |
|----------|--------|
| `VITE_AUTH_CLIENT_ID` | OAuth public client (`ai-spend-web`) |
| `CREDENTIAL_ENCRYPTION_KEY` | Kryptering af provider API-keys |
| `AISPEND_HOST` | Traefik-host (Dokploy) |
| `AISPEND_NETWORK_SUBNET` | Privat compose-subnet |

## Forbindelser

1. Log ind i web
2. **Forbindelser** → tilføj Cursor Admin API-nøgle og/eller OpenAI Admin-nøgle
3. Klik **Backfill** (eller vent på 15-min sync)

## API (udvalg)

- `GET /api/usage/summary?from&to`
- `GET /api/usage/series?groupBy=model|provider|user`
- `GET /api/usage/events`
- `GET /api/meters`
- `POST /api/ingest/cursor/backfill`
- `POST /api/ingest/openai/backfill`
