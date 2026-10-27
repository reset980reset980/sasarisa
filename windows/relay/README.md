# Internet phone connection

This free, self-hosted relay adds Internet connectivity alongside the existing LAN server. Both the Windows PC and phone connect outward over HTTPS. Neither port forwarding nor access to the same Wi-Fi is required. A workplace must allow access to the selected HTTPS origin; this does not bypass network policies.

`server.mjs` uses Node 22 built-in modules only. Set `PUBLIC_ORIGIN` to an exact HTTPS origin, `WEB_ROOT` to `PhoneWeb`, and `SOURCE_ARCHIVE` to the corresponding source zip. It binds loopback port 17846 behind a TLS reverse proxy. The proxy must preserve Host and replace untrusted X-Forwarded-For. The example service uses `https://sasarisa.xsw.kr`.

The PC opens a session explicitly and receives a random 12-character pairing code (10 minutes) and a separate 256-bit desktop credential. Phones receive their own 256-bit tokens. Up to four phones share one PC session, lasting at most eight hours. Closing/resetting the PC connection revokes all credentials. Server restart loses every session; reconnect explicitly. A PC absent for three minutes is removed. Commands and replies are RAM-only on the relay; the Windows durable receipt ledger prevents duplicate execution. The server operator can process plaintext commands/replies inside the HTTPS endpoint; this is not end-to-end encryption.

The browser microphone button uses Korean Web Speech recognition with an explicit user gesture and permission. It stops after 30 seconds or when the page hides, and requires the user to review and send the transcript. The browser provider may process audio on its recognition servers. Chrome on Android and Safari on iOS are target browsers; availability, microphone grants, Siri settings and physical-device accuracy require device verification. Unsupported browsers retain keyboard input. LAN HTTP retains OS-keyboard dictation.

References: [Web Speech API](https://developer.mozilla.org/en-US/docs/Web/API/Web_Speech_API/Using_the_Web_Speech_API), [Safari speech recognition](https://webkit.org/blog/11648/new-webkit-features-in-safari-14-1/).

Run protocol checks: `node --test windows/relay/server.test.mjs`.

## Deployment and restoration

Copy the relay, web assets and source archive to `/home/reset980/project/sasarisa-relay`. Install the three included user units under `~/.config/systemd/user`, reload systemd, then enable `sasarisa-relay.service` and `sasarisa-relay-route.timer`. The route helper first makes and validates a private Caddy JSON snapshot. It appends only the host-specific `sasarisa-phone-relay` route; it refuses an existing different route. `/etc/caddy/Caddyfile` is unchanged. The user timer restores this route after a Caddy reload because editing the root-owned Caddyfile requires separate system access.

To restore: stop/disable `sasarisa-relay-route.timer`, run `python3 ensure-route.py --remove`, then stop/disable `sasarisa-relay.service`. This removes only this route and service. Existing services remain intact. Preserve the private snapshots for diagnosis; never publish them with the source archive. Back up this directory before subsequent releases, excluding transient/private backup contents from public source downloads.

Do not claim real phone or workplace network verification based on the protocol tests or desktop browser emulation.
