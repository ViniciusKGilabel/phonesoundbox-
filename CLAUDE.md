# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

PhoneBox turns an Android phone into a desk panel for a computer. It has three parts:

- **Desktop agent**: `agent/PhoneBox.Agent`, .NET 10 ASP.NET Core minimal API, assembly `PhoneBoxAgent`.
- **Android app**: `android/`, Kotlin. A WebView runs the panel UI from `android/app/src/main/assets/www`.
- **iOS companion**: `ios/PhoneBox`, experimental SwiftUI + `WKWebView`. It reuses the Android panel assets.

Read `CONTRIBUTING.md` before you change anything that affects security, privacy, settings or widgets. Its rules are enforced in review.

## This fork

This repository is `ViniciusKGilabel/phonesoundbox-`, a fork of `ersinkoc/phonebox`.

- **Remotes:** `upstream` points to the creator's repo and is fetch-only; its push URL is deliberately invalid. `gh` is pinned to this fork with `gh repo set-default`. Bring in upstream changes with `git fetch upstream && git merge upstream/main`.
- **Keep the fork's diff small:** switch features off at the root instead of deleting upstream code, so merges stay clean.
- **AI integrations are off.** `Features.AiIntegrations = false` in `agent/PhoneBox.Agent/Features.cs` switches off quotas, sessions, transcripts, permission hooks and approvals, GitHub and GitHub workflows:
  - their monitors are registered but never started, so no AI vendor token is read;
  - `RequestGate` answers 404 for their API paths (`Features.IsDisabledPath`);
  - `/api/catalog` hides their widgets and templates (`Catalog.Offered*Json`);
  - the settings page hides their cards when `/api/info` reports `features.ai == false`;
  - `AiHookCleanup` removes hooks this agent installed earlier.

  Upstream's tests for these features still run and pass. Do not delete them.
- **Local API secret** (`LocalAuth.cs`): every `/api` call on 127.0.0.1:8765 needs the per-install secret, except `GET /api/info`, which returns only the version without it.
  - The secret lives in `local-api.key` (owner-only) next to `config.json`.
  - Browsers never get the key itself. A one-time `/login?t=&n=&sig=` link sets an HttpOnly, SameSite=Strict session cookie (an HMAC of the issue time) that expires after 7 days. The tray, the macOS menu bar, a second launch of the agent, and the installers (which launch the agent again) open that link.
  - The paths open without the secret are an explicit list in `LocalAuth.IsOpen`. Deleting `local-api.key` and restarting rotates the key and signs every browser out.
  - Scripts read the file and send it as the `X-PhoneBox-Local` header.
  - A new local consumer of the API (an installer probe, a community add-on, a CI smoke check) must send that header. See `tools/smoke-agent.py` and the Unix smoke step in `.github/workflows/build.yml`.
- **Updates come from this fork** (`UpdateConfig.DefaultRepo`). A saved `updates.repo` of `ersinkoc/phonebox` is migrated to it.
- **Local tooling:** the .NET SDK is installed in `~/.dotnet`, so prefix commands with `PATH=$HOME/.dotnet:$PATH`. Run Python scripts with `/usr/bin/python3`; the Homebrew `python3` on this machine lacks `expat`.
- **Known failure:** `arabic-settings.spec.js:99` (textarea selection) also fails on unmodified upstream on macOS.

## Commands

Run all commands from the repo root unless a different directory is shown.

```sh
# Agent unit tests (xUnit, builds the cross-platform net10.0 target)
dotnet test agent/PhoneBox.Agent.Tests
dotnet test agent/PhoneBox.Agent.Tests --filter "FullyQualifiedName~ConfigTests"     # one class
dotnet test agent/PhoneBox.Agent.Tests --filter "FullyQualifiedName~ConfigTests.SomeMethod"

# Run the agent (settings UI at http://127.0.0.1:8765)
dotnet run --project agent/PhoneBox.Agent -f net10.0                              # Linux/macOS
dotnet run --project agent/PhoneBox.Agent -f net10.0-windows10.0.19041.0          # Windows (tray build)
#   Isolated run next to an installed agent: add  -- --config /tmp/x/config.json --port 18765

# Panel + settings preview with simulated data, no agent needed
python tools/demo-server.py      # http://127.0.0.1:8790/panel/  and  /settings/  (window 780x360 = phone)

# Browser UI tests (Playwright; builds the web demo and serves it on port 8781)
cd tools/ui-tests && npm ci && npx playwright install chromium && npx playwright test
npx playwright test panel.spec.js            # one file
npx playwright test -g "some test title"     # by name

# Release metadata/catalog consistency (versions, widget/template counts, locales)
python tools/check-release.py

# Android (JDK 17, SDK Platform 35; set ANDROID_HOME or android/local.properties)
cd android && ./gradlew lintDebug assembleDebug testDebugUnitTest
python tools/test-agent-stream.py     # real-Kotlin checks of AgentStream.kt (run after a Gradle build)
python tools/test-bridge-checks.py

# Packaging
./scripts/build.sh [osx-arm64|osx-x64|linux-x64|linux-arm64|all]   # macOS/Linux -> dist/
.\scripts\build.ps1                                                  # Windows: APK + agent archives
```

CI (`.github/workflows/build.yml`) runs `check-release.py`, the Android lint, build and Kotlin checks, Playwright, and the agent tests plus a published smoke test on win-x64, win-arm64, linux-x64 and osx-arm64. The installer regression scripts in `tools/test-*.py` and `tools/test-*.ps1` cover `setup.sh`, `setup.ps1` and the `scripts/` installers. Run them when you change those files.

## Architecture

**Data flow.** The panel JS never talks to the PC directly. It requests `/agent/*`, and the Android app (`AgentLink.kt`) intercepts and forwards those requests:
- **USB**: `adb reverse` to a loopback port that belongs only to the phone (`UsbLink.cs`). TLS is pinned by the app, and requests must carry the app's USB key.
- **Wi-Fi/Tailscale**: HTTPS on port 8766 with a pinned certificate and a paired key (`Lan.cs`, `Pairing.cs`).
- The settings server stays on `127.0.0.1:8765`. Settings, pairing and test endpoints are local only. Remote callers can reach only the paths in `Lan.AllowedOverLan`. Adding a path to that allow-list is a security decision that needs a justification.
- Live events (page switches, notifications, permission prompts) travel over one SSE stream, `/api/stream`. `AgentStream.kt` consumes it on the phone. Polling is the fallback.
- The agent samples only what is on screen. Process lists and command widgets run only while a phone shows them.

**Agent layout.** `Program.cs` wires DI and maps most endpoints. Each feature lives in its own file, usually a `*Monitor` or `*Control` class. Large features are split into partial files such as `ControlDeck.*.cs`, `SessionMonitor.*.cs` and `QuotaMonitor.*.cs`. The project has two target frameworks:
- `net10.0-windows10.0.19041.0` adds the tray icon, WinForms, NAudio and WinRT media (`Tray.cs`, `AudioControl.cs`, `MediaMonitor.cs`, `JobObject.cs`).
- `net10.0` compiles `Unix/` instead, which holds the Linux/macOS implementations. It uses invariant globalization.

The test project references the `net10.0` build, so Windows-only code is not unit-tested there.

**Config.** `Config.cs` holds the settings model and normalizes bad values. Defaults ship as embedded `config.default.json`, `config.default.linux.json` and `config.default.macos.json`. `AgentConfig.ToPublic()` builds the only view of settings that the phone receives. Secrets, commands, tokens and calendar URLs must never appear in it, and that includes Smart Stack children.

**Widgets.** Each widget has three parts:
- A catalogue entry (name, size, options) in `agent/PhoneBox.Agent/widgets.json`. This file drives the settings editor in `settings.html`, a single large file.
- A `{ needs, create(box, ctx) → { update(kind) } }` entry in the `W` registry, under `android/app/src/main/assets/www/widgets/*.js`.
- Its styles in `style.css`.

`main.js` handles pages, navigation and agent data. Templates live in `templates.json`, on a 12×6 grid. The panel must run in Chromium 83+ (`compat.js` provides polyfills). Outside the app, with no native bridge, a widget must render an empty state instead of throwing.

**Web demo.** `tools/pages/build.py` assembles the real panel and settings page into a static demo, using mock agent data from `tools/pages/demo.js`. The demo server, the GitHub Pages demo and the Playwright tests all use this same demo, so a new widget that calls the agent needs a mock in `demo.js`.

## Checklists from CONTRIBUTING.md

- **New setting**: add it to `Config.cs` with a default and normalization, to all three `config.default*.json` files, and to the settings page. Decide whether it belongs in `ToPublic()`.
- **New widget**: add the `widgets.json` entry, the widget JS, an icon in `WIDGET_ICONS`, and a mock in `tools/pages/demo.js`. Also bump the widget count in the README (`**N widgets**`), which `check-release.py` verifies. The landing page computes its counts, so never hard-code them there.
- **Security model**:
  - The phone sends ids, never commands, paths or URLs. The agent looks the id up in its own settings.
  - Anything that controls the PC must sit behind a per-widget option that the agent checks via `Widgets(enabledPagesOnly: true)`.
  - New features that start programs, record or send data are off by default.
  - Start processes with `ProcessStartInfo.ArgumentList`, never through a shell, and kill them on timeout or cancel.
  - In the UI, build elements with `textContent`, never `innerHTML` with data.
- **Privacy**: every new outbound connection needs a row in `PRIVACY.md` with the host, what is sent and how to disable it.
- **Android**: `minSdk` is 21. Calls above API 21 need a version check, and `lintRelease`/`lintDebug` flags missing checks as NewApi.
- **i18n**: the panel and editor share `i18n.js` and the `locale-*.js` files, through `t()`, `tc()` and `tp()`. After you add a language, run `python tools/sync-locales.py`. Android `values-*/strings.xml`, `TrayStrings.*.resx` and `docs/README.*.md` must stay in sync.
- Leave `CHANGELOG.md` and version numbers alone; the maintainer updates them. The version lives in both `PhoneBox.Agent.csproj` and `android/app/build.gradle.kts`.
- Keep each PR to one change of about 800 lines or fewer, not counting tests. Write code, comments and UI text in English. Comments explain why, not what.
- Tests must not depend on the machine they run on: no fixed ports, real clocks, user settings or install paths.
