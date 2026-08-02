# Client obfuscation

Defends the `.NET` client against the casual reverser. Without this, a pirate
opens `PisoNetClient.exe` in [dnSpy](https://github.com/dnSpyEx/dnSpy), edits
`LicenseService.IsActive()` to `Return True`, saves, and every license check
we wrote is bypassed in under a minute.

With this in place, the binary is still attackable — obfuscation is a speed
bump, never a wall — but the time cost goes from "30 seconds" to "weekend
project for a competent reverser." That's usually enough to keep the software
out of casual sharing groups.

---

## What this protects against

| Attacker | Result |
|---|---|
| Casual user who wants to "remove the activation" via dnSpy | Blocked — can't find or edit the method |
| Patched build published on Facebook groups | Friction high enough that few people will bother |
| Determined reverser with a week of time | **Still wins.** Defense in depth is the only answer. |

The "determined reverser" case is why the JWT chain still matters: even if
they patch the client, they can't fake a token signed by pisonex.com's
private key — so the server-side activation flow still gates real use.

---

## One-time setup

### 1. Install .NET 8 SDK

```
dotnet --version
```

Should print `8.x.y`. If not, install from <https://dotnet.microsoft.com/download/dotnet/8.0>.

### 2. Download ConfuserEx (.NET 8-capable fork)

The original [ConfuserEx](https://github.com/yck1509/ConfuserEx) is abandoned
and only targets .NET Framework. You need a fork that supports .NET 8 / .NET
Core 3+.

As of writing, options to evaluate (verify each is still maintained when you
download):

- **[mkaring/ConfuserEx](https://github.com/mkaring/ConfuserEx)** — most
  widely used fork, broad .NET Core support, may need a custom build for
  .NET 8.
- **Community forks** — search GitHub for `ConfuserEx .NET 8` and pick a
  fork with recent commits and a release that includes `Confuser.CLI.exe`.

Extract the release archive so the structure looks like:

```
pisonex/client/tools/ConfuserEx/
  Confuser.CLI.exe
  Confuser.Core.dll
  Confuser.Protections.dll
  Confuser.Renamer.dll
  Confuser.Runtime.dll
  ... (other supporting files)
```

The build script expects `Confuser.CLI.exe` at exactly that path.

### 3. Add `tools/` to `.gitignore`

```
echo tools/ >> pisonex/client/.gitignore
```

ConfuserEx is a few MB. Don't commit it — every developer downloads their
own copy.

---

## Building an obfuscated release

From a regular `cmd.exe` or PowerShell:

```
cd pisonex\client
build-obfuscated.bat
```

Output: `build\client-obfuscated\` — ship that folder (or zip it).

The script:

1. Runs `dotnet publish` to a plain (non-single-file) folder.
2. Runs ConfuserEx against `PisoNetClient.dll` using `obfuscation\confuse.crproj`.
3. Copies the .NET runtime files alongside the obfuscated DLL so the result
   runs standalone.

---

## What's enabled (and what isn't)

See the comment block at the top of `obfuscation/confuse.crproj` for the
full rationale. Short version:

**Enabled (standard profile):**
- `ctrl flow` — static analysis of control flow becomes useless.
- `constants` — string and numeric literals are encrypted in the binary.
- `anti debug` — refuses to attach a debugger at runtime.
- `anti ildasm` — minor; sets `SuppressIldasm`.
- `rename` (`renPublic=true`) — scrambles class/method/field names,
  including public members. `IsActive`, `LicenseService`, `GetDeviceId`
  etc. become single-letter names, so dnSpy users can't navigate by name.
  The JSON DTO classes are excluded at the source via
  `<Obfuscation(Exclude:=True, ApplyToMembers:=True)>` attributes
  (`HeartbeatResponse`, `MemberLoginResponse`, `MemberLogoutResponse`,
  `MemberChangePasswordResponse`) so System.Text.Json deserialization keeps
  working. The metrics DTOs are safe automatically because they pin JSON
  keys with `<JsonPropertyName>` attributes.

**Deliberately disabled:**
- `anti tamper` — would conflict with Authenticode code-signing (which
  is the next thing to add). Pick one of the two.

---

## Testing checklist

Run the obfuscated build on a clean Windows 10/11 VM before shipping. The
checklist:

- [ ] Application starts (no immediate crash).
- [ ] First-run dialog appears and saves.
- [ ] Heartbeat reaches the local FastAPI server (PC shows online in dashboard).
- [ ] Coin-insertion adds time (server -> client unlock flow works).
- [ ] License activation succeeds against pisonex.com.
- [ ] License token is written to `%ProgramData%\PisoNet\license.dat`.
- [ ] Verify timer fires after restart (check log: "License verification: …").
- [ ] Admin panel opens with PIN.
- [ ] Lock screen wallpaper loads.
- [ ] WMI calls work (Open admin panel -> License tab -> Device ID is non-empty).
- [ ] Trial-anchor gate behaves: disconnect from internet on a fresh install,
      confirm app refuses to start.

If anything misbehaves, the most likely culprits are:

1. **String encryption breaking WQL queries** — unlikely, but if so, exclude
   `HardwareFingerprint` from `constants` protection.
2. **Anti-debug false positive** under certain antivirus tools — try disabling
   `anti debug` first.
3. **Control flow breaking something with `Try/Catch` patterns** — extremely
   rare on conservative profile; report to the ConfuserEx fork issue tracker.

---

## Escalating protection later

Once the conservative profile is verified stable, the high-value next steps:

### Add renaming (with exclusions)

Most impactful single protection. Add this rule before the global one in
`confuse.crproj`:

```xml
<rule pattern="namespace() = 'PisoNetClient.Services' and (name() = 'HeartbeatResponse' or name() = 'ActivateResult')" preset="none" inherit="false" />
<rule pattern="namespace() = 'PisoNetClient.Services' and name() = 'TokenClaims'" preset="none" inherit="false" />
```

Then re-enable rename in the global rule:

```xml
<protection id="rename" />
```

Test the full checklist again — JSON deserialization is the usual breakage.

### Authenticode code signing

Sign the obfuscated `PisoNetClient.exe` (and .dll) with a code-signing
certificate. Done AFTER obfuscation. Stops the "unknown publisher" SmartScreen
warning and makes silent EXE replacement detectable.

### Move signing key to KMS

The single biggest long-term risk is leakage of
`LICENSE_SIGNING_PRIVATE_KEY` from pisonex.com's env. Move it to AWS KMS,
GCP KMS, or Cloudflare. Plan key rotation (`kid` claim in the JWT) before
the first paid release.

---

## What this does NOT cover

Obfuscation is one layer. The complete defense includes:

| Layer | Status |
|---|---|
| Hardware fingerprint fuzzy-match | ✅ Done |
| Trial-anchor gate | ✅ Done |
| Token-signature-required IsActivated | ✅ Done |
| **.NET obfuscation (this directory)** | ✅ Done — conservative profile |
| Authenticode code signing | ⬜ Next |
| Renaming with exclusions | ⬜ After conservative is verified stable |
| Anti-tamper OR signing (mutually exclusive) | ⬜ Pick one |
| KMS for signing key | ⬜ Before public release |
| Server-side fingerprint anomaly detection | ⬜ Phase 2 |
