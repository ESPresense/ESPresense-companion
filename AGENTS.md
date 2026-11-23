# Repository Guidelines

## Specifications

- Work specs first: update the relevant document in `specs/` before changing implementation or tests. Create a focused spec when none exists.
- Explain **why** alongside the rules: the problem, constraints, failure modes, and reasons for important choices belong in the spec.
- Follow Hermes Converse spec conventions: state intended behavior directly, use established terminology, and show concrete observable examples. Keep each spec small and focused.
- Write specs as the agreed contract. Do not qualify rules with "currently", recount implementation history, or add proposed sections and abandoned designs.
- Keep behavior and design documentation in specs. Use code comments for brief, non-obvious local details and link to the relevant spec instead of repeating it.
- Keep specs, implementation, and tests consistent. A spec-only request authorizes documentation changes, not implementation or deployment.

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
