# Code signing with SignPath

Releases are signed through [SignPath.io](https://about.signpath.io), using the free
[SignPath Foundation](https://signpath.org) program for open-source projects. Signed binaries remove the
"Unknown publisher" warning and let SmartScreen build reputation for the app.

The release workflow (`.github/workflows/build.yml`, job `release`) is already wired up. It signs only
when the secrets below exist; until then, tagged releases are built unsigned exactly as before.

## What gets signed

| Step | SignPath artifact configuration | What is signed |
|---|---|---|
| 1 | `app-binaries` ([.signpath/artifact-configurations/app-binaries.xml](../.signpath/artifact-configurations/app-binaries.xml)) | `DiskAnalyzer.exe`, `DiskAnalyzer.dll`, `DiskAnalyzer.*.dll` in the self-contained publish folder |
| 2 | `installer` ([.signpath/artifact-configurations/installer.xml](../.signpath/artifact-configurations/installer.xml)) | `DiskAnalyzer-Setup-x64.msi`, built from the already signed binaries |

The portable zip contains the signed binaries from step 1. Microsoft runtime files are already signed by Microsoft
and are not re-signed.

## One-time setup (maintainer)

1. **Apply to SignPath Foundation:** https://signpath.org/apply. Requirements include an OSI-approved license
   (MIT ✔), a public repository, builds on a hosted CI system (GitHub Actions ✔), an existing release (v1.0.1 ✔),
   and the code signing policy section in the README (✔ already added). Approval is manual and takes some time.
2. **Once approved**, SignPath creates an organization and a project for you. In the SignPath web app:
   - Note your **Organization ID** (Settings → Organization).
   - Make sure the project slug is `disk-analyzer`, or set the `SIGNPATH_PROJECT_SLUG` variable below to the actual slug.
   - Link the project's trusted build system to **GitHub.com** for `tinkubhanu216/disk-analyzer`, as described in SignPath's GitHub integration docs.
   - Create two **artifact configurations** with slugs `app-binaries` and `installer`, pasting the XML files
     from `.signpath/artifact-configurations/`. SignPath validates the XML when you save; adjust it there if needed.
   - The signing policy is usually `release-signing`. If yours differs, set `SIGNPATH_SIGNING_POLICY_SLUG`.
   - Create an **API token** for a CI user that is allowed to submit signing requests for the project.
3. **In GitHub** (repository → Settings → Secrets and variables → Actions):
   - Secret `SIGNPATH_API_TOKEN` = the API token.
   - Variable `SIGNPATH_ORGANIZATION_ID` = the organization ID.
   - Optional variables: `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG`.

## Releasing a signed version

1. Bump `<Version>` in `Directory.Build.props` and `ProductVersion` in
   `installer/DiskAnalyzer.Installer/DiskAnalyzer.Installer.wixproj`, then commit.
2. Push a tag: `git tag -a v1.0.2 -m "Disk Analyzer 1.0.2"` and `git push origin v1.0.2`.
3. The `release` job submits two signing requests. SignPath Foundation's release policy requires a **manual
   approval** in the SignPath web app for each one. Approve them there; the job waits up to an hour for each.
4. The job verifies the signatures (`Get-AuthenticodeSignature`) and creates a **draft** release with the signed
   MSI, the portable zip and `SHA256SUMS.txt`. Review it and publish.

## Notes

- With SignPath Foundation, the certificate is issued to **SignPath Foundation**, so Windows shows that name as the
  publisher (not the maintainer's personal name). This is how the free program works.
- SmartScreen reputation is tied to the certificate and builds up over downloads; the first signed releases may still
  show a warning for a while, but much less often than unsigned ones.
- Only binaries built by GitHub Actions from this repository are signed. Never sign locally built files or anything
  not produced by this project.
