# Repository Guidelines

## Project Structure & Module Organization
- Backend: C# ASP.NET Core in `src/` (controllers, services, models, utils).
- Frontend: SvelteKit app in `src/ui/` (routes, lib, tests). Vite dev server proxies `/api` and `/ws` to backend.
- Static assets: `src/wwwroot/` (served by backend in production).
- Tests: NUnit C# tests in `tests/`; frontend unit/e2e tests in `src/ui/src` and `src/ui/tests`.

## Build, Test, and Development Commands
- Backend dev: `dotnet watch --project src` (serves API/WebSocket at http://localhost:5279).
- Backend build/test: `dotnet build` • `dotnet test`.
- Frontend setup/dev: `pnpm -C src/ui install` • `pnpm -C src/ui start` (Vite dev at http://localhost:5173).
- Frontend build/preview: `pnpm -C src/ui build` • `pnpm -C src/ui preview`.
- Lint/format UI: `pnpm -C src/ui lint` • `pnpm -C src/ui format`.
- Optional container: `docker build -t espresense/companion .` (uses repo `Dockerfile`).

## Coding Style & Naming Conventions
- EditorConfig: C# uses spaces, 4-width; UI (`*.svelte, *.ts, *.js`) uses tabs, width 2.
- C#: PascalCase for types/methods, camelCase for locals/params; `*Controller.cs`, `*Service.cs` patterns.
- Svelte/TS: Components PascalCase (`Map.svelte`), stores/utilities in `src/ui/src/lib`.
- Formatting: UI uses Prettier + ESLint; keep files clean before PR.

## Testing Guidelines
- Backend: NUnit (+ Moq). Place tests under `tests/` with `*Tests.cs` naming. Example: `TimeSpanExtensionsTests.cs`.
- Frontend: Vitest unit tests (`pnpm -C src/ui test:unit`) and Playwright e2e (`pnpm -C src/ui test`).
- Expectations: Add/extend tests for new logic; keep existing tests green.

## Commit & Pull Request Guidelines
- Commits: Imperative, concise subject (≤72 chars). Prefer Conventional Commits (e.g., `feat:`, `fix:`) when reasonable. Link issues (`Closes #123`).
- PRs: Include purpose, scope, testing notes, and screenshots/GIFs for UI changes. Reference related issues/PRs.
- Quality gate: run `dotnet test`, `pnpm -C src/ui lint`, and relevant UI tests before opening/review.

## Security & Configuration Tips
- Do not commit secrets. Use `src/config.example.yaml` as a template; keep local config out of VCS.
- For local tweaks use `appsettings.json`; avoid hardcoding credentials. Review `.gitignore` before adding new files.
