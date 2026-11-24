# Packaging

Templates for package managers. Fill in `VERSION` and the `SHA256` values from the `.sha256` files attached to a GitHub release, then submit:

| Manager | File | Submit to |
| --- | --- | --- |
| winget | `winget/Haidarsf.Overseer.*.yaml` | a pull request to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) (or `wingetcreate submit`) |
| Scoop | `scoop/overseer.json` | your own bucket, or a pull request to the Extras bucket |
| Homebrew | `homebrew/overseer.rb` | your own tap (`Haidarsf/homebrew-tap`), then `brew install --cask haidarsf/tap/overseer` |

Package managers expect signed binaries (winget especially), so sign the release first.
