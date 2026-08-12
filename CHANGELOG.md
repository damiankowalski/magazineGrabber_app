# Changelog

All notable changes to this project are documented here.
The format is loosely based on [Keep a Changelog](https://keepachangelog.com/).

## [2.1.0]

### Added
- **Persistent login session.** After you log in to stare.e-gry.net once, the session
  is saved **encrypted** (Windows DPAPI, scoped to your Windows account) next to the
  EXE, and the embedded browser keeps its own profile. On the next run downloads
  usually start with **no login at all**; when the session finally expires,
  re-authenticating is a single "I'm logged in" click - you never re-type your
  password. The encrypted blob can't be read on another machine or account, and it
  stores the session, not your password.

### Fixed
- **Being asked to log in twice in a row, and again for every new selection.** The
  retry logic no longer throws away a fresh session at the first hiccup - it retries
  a couple of times before ever re-opening the login window, so one successful login
  is enough. Combined with the persisted session above, repeated logins are gone.

## [2.0.1]

### Fixed
- **stare.e-gry.net: only the first download in a session worked; the next ones
  failed until the app was restarted.** The login cookies now keep the browser's own
  domain/path scoping and the cookie jar is cleared on each login, so the server's
  follow-up `Set-Cookie` updates the session instead of creating a duplicate the
  server then reads as the wrong session. On top of that, a failed or
  "please log in" download on a login-based site now **re-establishes the session
  automatically** (the same thing restarting the app was doing) and retries, and a
  `Referer` header plus a small inter-request delay reduce the chance of tripping a
  per-session throttle.

## [2.0.0]

### Added
- **App icon** (`app.ico`) wired as the EXE icon and both window icons.
- **Per-item progress bars** in the grid, plus an indeterminate (marquee) state
  during conversion phases that have no page-by-page feedback.
- **Live listing progress** — the status line shows metadata-reading progress for
  large archive.org collections while the overall bar runs indeterminate.
- **Staged logging** with bold section headers: *Loading list*, *Downloading*,
  *Summary*, *Results*.
- **Recognized-vs-produced summary**: recognized item counts by format at list time,
  then downloaded / PDFs-ready / DjVu-left / failed after a batch.
- **End-of-batch results list** of every generated PDF and every DjVu left behind,
  with each file line **double-clickable to open**.
- **Concurrency slider** (1–5, default **1**), clamped per provider
  (stare.e-gry.net is forced to 1 for its session model).
- **Cancel** button (wires the previously unused cancellation token) and an
  **Open folder** button.
- **Automatic DjVu → PDF** via DjVuLibre's `ddjvu` when it is on `PATH`
  (optional; graceful fallback to keeping the DjVu when absent).
- Empty URL box with an inline **placeholder hint**, and stronger URL validation
  with clear error messages.

### Fixed
- **First download after login failing on stare.e-gry.net.** Login cookies from the
  embedded browser are now re-scoped host-only so they cleanly replace the earlier
  anonymous session cookie instead of duplicating it; the per-item retry budget also
  retries a few times right after a fresh login, which automates the old
  "click Start a second time" workaround.
- **archive.org items with nested file paths** (a single item bundling many magazines
  in subfolders, e.g. `click-1999-2006`) failing with "Could not find a part of the
  path". Source files are now saved flat under the item's `source\` folder, and the
  remote download URL escapes each path segment while keeping the `/` separators.

## [1.0.0]

### Added
- Initial release: provider pattern (archive.org + stare.e-gry.net), WebView2 login,
  JP2 → PDF conversion (img2pdf with a Pillow fallback), overall progress + color log.
