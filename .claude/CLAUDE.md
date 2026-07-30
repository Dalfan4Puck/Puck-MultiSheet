# MultiSheet — agent notes

## GitHub push (read this before any commit/push)

**Never run bare `git push`.** It fails with `Permission denied (publickey)` because the SSH key passphrase is not interactive in agent shells.

### Preferred (commit + push)

From repo root (`DALF MOD LIBRARY/Dalf Multisheet`):

```powershell
.\tools\sync-git.ps1 -Message "Your commit message"
```

Or after a build:

```powershell
.\deploy-qa.ps1              # build + commit + push
.\deploy-qa.ps1 -SkipGit     # build only
```

Both use `tools/git-push-common.ps1` → `Push-MultiSheetGit`.

### Push only (commit already exists)

If you already committed and only need to push:

```powershell
$key = "C:\Users\bhsda\OneDrive\Desktop\Puck Mod\Server & SQL Files\id_rsa"
$passFile = "C:\Users\bhsda\OneDrive\Desktop\Puck Mod\Server & SQL Files\tools\github_ssh_passphrase.txt"
$pass = (Get-Content $passFile -First 1).Trim()
$askpass = Join-Path $env:TEMP "phl-git-askpass-$PID.cmd"
[System.IO.File]::WriteAllText($askpass, "@echo $pass")
$env:SSH_ASKPASS = $askpass
$env:SSH_ASKPASS_REQUIRE = "force"
$env:DISPLAY = "1"
$env:GIT_SSH_COMMAND = "ssh -i `"$key`" -o IdentitiesOnly=yes"
git push -u origin main
Remove-Item $askpass -Force -ErrorAction SilentlyContinue
Remove-Item Env:SSH_ASKPASS, Env:SSH_ASKPASS_REQUIRE, Env:DISPLAY, Env:GIT_SSH_COMMAND -ErrorAction SilentlyContinue
```

### Credentials (local only — never commit)

| File | Purpose |
|------|---------|
| `Server & SQL Files/id_rsa` | SSH private key (GitHub + VPS) |
| `Server & SQL Files/tools/github_ssh_passphrase.txt` | One-line key passphrase |
| `Server & SQL Files/tools/multisheet-deploy.json` | `SshKey` path (gitignored here) |

Override key path: `$env:MULTISHEET_SSH_KEY` or `$env:MULTISHEET_DEPLOY_CONFIG`.

### Remote

- `git@github.com:Dalfan4Puck/Puck-MultiSheet.git`
- Branch: `main`

### Full operator guide (outside this repo)

`Guides And Documentation/Pushing to Dalfan4Puck Github.md` in the Puck Playground tree.

### Troubleshooting

| Error | Fix |
|-------|-----|
| `Permission denied (publickey)` | Use SSH_ASKPASS flow above, not plain `git push` |
| `github_ssh_passphrase.txt not found` | File must exist beside deploy config |
| Public git audit failed | Remove VPS IPs, scp paths, `id_rsa` refs from staged files |
