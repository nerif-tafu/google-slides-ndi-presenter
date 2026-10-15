# Google Slides NDI Presenter

This is a Windows app for running a Google Slides deck into a vision mixer over NDI. It sends two sources:

- **GSlides Slides** is the presentation itself at 1080p, with animations, transitions and video playing as they would in Slides.
- **GSlides Notes** is a presenter view: the current slide, the next slide and your speaker notes.

You drive it from the app window, the keyboard, a presentation clicker, a phone on the same network, or over HTTP from something like Companion or a Stream Deck.

## What you need

- Windows 10 (version 2004 or later) or Windows 11, 64-bit.
- The NDI 6 runtime. Installing [NDI Tools](https://ndi.video/tools/) gets you this.
- The Microsoft Edge WebView2 runtime. Windows 11 already has it.
- The [.NET 9 SDK](https://dotnet.microsoft.com/download) if you're building from source.

The simplest setup is a deck shared as "Anyone with the link can view", which works without signing in. For decks that are only shared with particular people, sign in to a Google account that can open them (see [Private decks](#private-decks)).

## Download

Grab `GoogleSlidesNdiPresenter-<version>-win-x64.exe` from the [Releases](https://github.com/nerif-tafu/google-slides-ndi-presenter/releases) page. It's a single file with everything bundled, so there's nothing to install beyond the NDI and WebView2 runtimes above. Windows SmartScreen may warn you the first time because the exe isn't code-signed.

## Building and running

```bash
dotnet run -c Release --project GoogleSlidesNdiPresenter
```

To make a folder you can copy to another PC:

```bash
dotnet publish GoogleSlidesNdiPresenter -c Release -r win-x64 --self-contained -o publish
```

## Using it

Paste the deck's share link (the normal `docs.google.com/presentation/d/.../edit` one) and press Open. The two monitors in the middle of the window show exactly what each NDI source is sending.

Next and Previous behave like a clicker: Next plays the slide's next animation and only moves on once there are none left. Click any slide in the strip at the bottom to jump to it, or type its number and press Enter.

Under the presenter view there's a text size control for the notes, an Auto-fit switch that makes each slide's notes as big as will fit, and buttons to scroll long notes.

If a slide has a video or audio clip, a Play button appears on the slide monitor with how far through it is, next to a button that takes it back to the start. Audio set to play on click in Slides only starts when you press Play here. While the slide is making any sound, a small level meter shows on the right of the monitor.

The (i) next to the deck's name lists every slide with video or audio: what it is, whether it starts automatically, on click or manually, whether its sound is muted, and whether it can actually play. Slides only plays a Drive video or audio file for people who can open that file, even if they can open the deck, so a clip that isn't shared with the Google account signed in here (or publicly) shows as "No access". Click it to open Google's "You need access" page on that account, with Viewer picked, and press Request access to ask the owner. The same button appears on the message you get when the presentation itself can't be opened. YouTube videos that are private, removed or not allowed to be embedded are flagged too. When anything can't play, the (i) turns red. It's worth a look before the show so nothing catches you out.

If someone edits the deck in Google Slides while it's open, the app notices within about 30 seconds: the Refresh button gets an amber dot and a message pops up. Press Refresh to pull the changes in. You stay on the slide you were on. The app never refreshes by itself, so nothing changes on air until you decide. Close (Ctrl+W) puts both outputs back to a holding screen ("The presentation will begin shortly" on the slides feed). If you close by accident, the message that pops up has a Reopen button.

The app remembers recent decks, the last one you had open, your notes settings and the window position.

### Keyboard

| Keys | Does |
|---|---|
| → or Space or PgDn | Next |
| ← or PgUp | Previous |
| Home / End | First / last slide |
| A number, then Enter | Go to that slide |
| P | Play or pause the video or audio |
| Shift+P | Video or audio back to the start |
| + / − | Bigger / smaller notes |
| A | Auto-fit notes on or off |
| ↑ / ↓ | Scroll the notes |
| R | Refresh |
| Ctrl+W | Close the presentation |
| Ctrl+O | Open a presentation |
| Ctrl+, | Settings |
| ? | List the shortcuts |

### From a phone or tablet

The sidebar shows one or more addresses under Remote control. Open one in a browser on any device on the same network and you get the same controls, laid out for a small screen. Settings can only be changed on the PC running the app.

## Private decks

If a deck isn't shared as "Anyone with the link", go to Settings → Google account and press Sign in. A small window opens with Google's normal sign-in page. Sign in there with an account that can open the deck, and the window closes on its own once you're in. After that, decks shared with that account open the same way as public ones, with notes, previews, animations and video. Public decks keep working whether you're signed in or not.

One account is signed in at a time. Sign out from the same place. If a deck won't open, the app tells you whether it isn't shared publicly (sign in) or isn't shared with the account you're signed in with (ask the owner to share it).

The app doesn't keep a password or token of its own. Your Google session lives in the app's built-in browser, in its own profile, the same way a browser remembers you. Windows encrypts it for your user account. Signing in and out can only be done in the app window, never from a phone or over the API. A second copy started with `--settings` has its own sign-in.

## Audio

Sound from videos (and any audio on the slides) doesn't come out of Windows' default speakers on its own. The app takes it over and plays it where you tell it to. In Settings → Audio you can pick:

- an **Output device**, which would normally be the interface feeding your mixer. This starts out as the system default.
- a **Monitor device**, for example headphones for the operator. This starts out off.

Each one has its own volume. Changes apply as soon as you make them, so you can set levels while a video is playing. Audio isn't sent over NDI.

## Settings

Settings also covers the NDI source names, output resolution and frame rate, the remote control port, and whether other devices are allowed to connect at all. Apart from audio, these need a restart, which the Save button does for you.

If your network uses an NDI discovery server, put its address in **Discovery server** (for example `192.168.1.10`, or `ndi-ds.local:5959` if it isn't on the default port; separate several with commas). Leave it empty and NDI finds receivers automatically, the same as before. The setting only applies to this app. Everything else from NDI Access Manager (groups, adapters, multicast and so on) still applies, and other NDI apps on the PC aren't affected.

Everything is saved in `%APPDATA%\GoogleSlidesNdiPresenter\settings.json`.

To run a second copy with different source names and its own port, give it its own settings file:

```bash
GoogleSlidesNdiPresenter.exe --settings "C:\path\to\second-settings.json"
```

## HTTP API

The app listens on port 8080 by default. Every action takes a GET or a POST and replies with the current state as JSON, so you can paste these straight into Companion.

| Endpoint | What it does |
|---|---|
| `/api/next`, `/api/previous` | Next / previous (`/api/prev` works too) |
| `/api/goto/{n}` | Go to slide n, counting from 1 |
| `/api/load?url=...` | Open a presentation. You can also POST `{"url": "..."}` |
| `/api/refresh` | Reload the open presentation |
| `/api/close` | Close it and show the holding screen |
| `/api/video/play`, `/pause`, `/toggle`, `/restart` | Control the video or audio on the current slide |
| `/api/notes/increase`, `/decrease` | Notes text 4px bigger / smaller |
| `/api/notes/size/{px}` | Set the notes size (12 to 300) |
| `/api/notes/autofit/on`, `/off`, `/toggle` | Auto-fit notes |
| `/api/notes/scroll/up`, `/down`, `/top` | Scroll the notes |
| `GET /api/state` | The current state |
| `GET /api/preview/slide.png`, `/notes.png` | A still of each output |

The state includes `outOfDate`, which is true when the deck has been edited in Google Slides since it was opened or last refreshed.

Some of what the app's own interface uses could also be handy elsewhere: `/api/events` (state as Server-Sent Events), `/api/meter` (audio levels), `/api/stream/slide.mjpg` and `/notes.mjpg` (live previews), and `/api/slides/{n}.jpg` (slide thumbnails).

There's no password on the API. If you're on a network you don't trust, turn off "Allow other devices" in Settings and only this PC will be able to control the app. Windows may ask about the firewall the first time it runs. If port 8080 is taken, the app still works, but remote control stays off until you choose another port.

## How it works

The slides feed is Google's own embedded Slides player running in a hidden browser (WebView2). The app records its screen with the Chrome DevTools screencast API and sends each frame over NDI. That's why animations and video look the same as they do in Slides.

Speaker notes and the next-slide previews come from Google's PowerPoint and PDF exports of the deck. They're downloaded when you open it and again when you refresh.

Google keeps a revision number for every deck that goes up each time it's edited. While a deck is open, the app reads it from the deck's page every 30 seconds, and if it's higher than when you opened or refreshed the deck, the deck is out of date.

Videos are locked to the best quality Google offers for them. Google sets that limit, though; the Drive video in the test deck, for example, only goes up to 720p.

For audio, a small script inside the player routes each video's sound into the app instead of the speakers. The app then plays it on the devices you picked.

NDI reports connections rather than receivers, and each receiver opens two, so the receiver numbers in the sidebar are that count halved.

## Cutting a release

Bump `<Version>` in `GoogleSlidesNdiPresenter.csproj`, then publish a release on GitHub with a tag like `v1.2.0`. The Release workflow builds the exe from that tag (taking the version from the tag) and attaches it to the release a few minutes later. If an asset ever needs rebuilding, run the workflow by hand from the Actions tab and give it the tag.

## Known limitations

- "Publish to web" links (the ones with `/d/e/` in them) can't be used. Use the share link instead.
- Google's sign-in page can refuse to work inside apps. If it ever does, share the deck as "Anyone with the link can view" instead.
- The next-slide preview shows the slide as it looks once all its animations have played.
- The slides feed goes through JPEG compression on the way out of the browser, so very fine detail can soften slightly.
- Audio runs about a tenth of a second behind the picture.
