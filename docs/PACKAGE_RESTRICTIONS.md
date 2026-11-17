# Package restrictions

An administrator can limit UniGetUI to a list of allowed packages. On a restricted machine:

- Search, the Software Catalog, package bundles, `unigetui://` links and the command line only
  offer the packages a rule allows.
- A package that is **already installed** stays visible and can still be updated, reinstalled and
  uninstalled, whether a rule allows it or not.
- Installing anything else is refused before the operation starts.

Restrictions limit what UniGetUI offers. They do not stop someone from running `winget`, `choco`
or another package manager directly; use the operating system's own controls for that.

## Rules

A rule names a package manager, optionally a source, optionally a package:

| Rule | Allows |
| --- | --- |
| `winget\|winget\|Mozilla.Firefox` | one package from one source |
| `winget\|*\|7zip.7zip` | one package from any source of the manager |
| `winget\|winget\|Microsoft.*` | every package of the source whose id matches (`*` is a wildcard) |
| `scoop\|main` | every package of a source |
| `pip` | every package of a package manager |

- The manager is its id, name or display name (`winget`, `Winget`, `WinGet`), as in package bundles.
- Nothing is case-sensitive.
- Only the id may contain `|`; it is whatever follows the second separator.
- When restrictions are on, what no rule allows is refused. An empty list allows no new package.
- `msstore` is a separate WinGet source and needs its own rule.
- The `installer` and `pkg` managers (catalog feeds, see [WINDOWS_INSTALLER_FEEDS.md](WINDOWS_INSTALLER_FEEDS.md)
  and [PKG_FEEDS.md](PKG_FEEDS.md)) follow the rules like any other. `installer` or `pkg` alone
  allows everything your feeds publish.

## Deploying with group policy (Windows)

Copy `InstallerExtras/PolicyDefinitions/Devolutions.UniGetUI.admx` and its `en-US` folder to your
central store (`\\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions`) or import them into Intune,
then set **Computer Configuration > Administrative Templates > UniGetUI > Restrict the packages
that can be found and installed**:

| State | Effect |
| --- | --- |
| Enabled | Only the listed rules are allowed. |
| Disabled | No restriction, even if a `PackageRestrictions.json` is present. |
| Not configured | The file below decides. |

The policy writes to `HKLM\SOFTWARE\Policies\Devolutions\UniGetUI\PackageRestrictions`:

| Value | Type | Meaning |
| --- | --- | --- |
| `Enabled` | `REG_DWORD` | `1` restricts, `0` turns restrictions off. Absent: not configured. |
| `AllowedPackages\<any name>` | `REG_SZ` | One rule per value. The value names are ignored. |

Any tool that writes these values works; the templates are a convenience.

## Deploying a file (any platform)

Without a configured policy, UniGetUI reads `PackageRestrictions.json` from its machine-wide folder:

| Platform | Path |
| --- | --- |
| Windows | `%ProgramData%\Devolutions\UniGetUI\PackageRestrictions.json` |
| macOS | `/Library/Application Support/Devolutions/UniGetUI/PackageRestrictions.json` |
| Linux | `/etc/UniGetUI/PackageRestrictions.json` |

```json
{
  "version": 1,
  "enabled": true,
  "allow": [
    { "manager": "winget", "source": "winget", "id": "Mozilla.Firefox", "name": "Mozilla Firefox", "version": "131.0" },
    { "manager": "winget", "source": "winget", "id": "Microsoft.*" },
    { "manager": "scoop", "source": "main" },
    { "manager": "pip" }
  ]
}
```

`name` and `version` are for whoever reads the file and are ignored. Comments and trailing commas
are accepted. `"enabled": false` turns the restrictions off.

**The file must be writable by administrators only**, or it is ignored: a file a standard user can
create or edit says nothing about what the organisation allows. Create it, and its folder, from an
elevated process (or as `SYSTEM`/`root`).

- Windows: the file and every folder above it must be owned by `SYSTEM`, `Administrators` or
  `TrustedInstaller`, and nobody else may modify the file or delete, rename or re-permission a
  folder. A `%ProgramData%\Devolutions\UniGetUI` folder created by an administrator qualifies; one
  created by a standard user does not.
- macOS and Linux: neither the file nor any folder above it may be writable by its group or by
  others, and the file may not be a symbolic link.

| What UniGetUI finds | Result |
| --- | --- |
| No policy, no file | No restriction. |
| A file standard users can modify | Ignored, with a warning in the log. No restriction. |
| A trusted file that cannot be parsed, or of another `version` | Nothing new can be installed, until it is fixed. |
| A rule that names no manager | That rule is skipped, with a warning in the log. |

**Settings > Administrator** shows which of these applies on the machine. Policy and file are read
again at most every 30 seconds, so a change needs no restart.

## Building the list from what is installed

To seed the list, export what is installed on a reference machine, or on several:

```
uniget export-installed C:\Temp\installed.json
uniget export-installed C:\Temp\installed.txt --format rules
uniget export-installed C:\Temp\all.json --merge \\server\share\PackageRestrictions.json
```

| Option | Meaning |
| --- | --- |
| `<file>` or `-` | Where to write; `-` is standard output. |
| `--manager <name>` | Only this package manager. Can be repeated. |
| `--format json` | A `PackageRestrictions.json` (default). |
| `--format rules` | One `manager\|source\|id` line per package, to paste into the policy's list. |
| `--merge <file>` | Start from an existing restrictions file and add what it lacks. Existing entries are kept as they are. |
| `--timeout <seconds>` | How long to wait for the package managers (120 by default). |

The command needs no running UniGetUI. It exits with `0` on success, `1` when the file could not
be written or the file to merge could not be read, `2` on a usage error, and `3` when the list was
written but a package manager failed or did not answer in time.

Use `uniget` in scripts: `UniGetUI.exe` is a graphical program and does not make the shell wait.

The same list is available from the **Installed packages** page (**Export as allow-list**, for the
checked packages or the whole filtered list) and from the terminal UI.

Things to know about an export:

- Run it as the user. Run as `SYSTEM` (as Intune does by default), it misses per-user package
  managers such as Scoop, pip and npm.
- Software UniGetUI found on the machine without a package manager source cannot be installed
  again, so it is left out; the command says how many.
- Chocolatey, .NET Tool and the catalog feeds do not record which source an installed package came
  from. Their packages are exported with `*` as the source.
- An export is a list of what one machine has, not a list of what is approved. Review it.

## Limits

- **Source names can be imitated.** Rules compare the source by name. Where a standard user can add
  or rename sources (a Scoop bucket, a per-user NuGet configuration), a source with an allowed name
  passes. Allow whole sources only for managers whose sources users cannot change.
- **Dependencies** are resolved by the package managers, not by UniGetUI. A dependency outside the
  list is installed with the package that needs it, and can then be updated like anything installed.
- **Results already on screen** stay until the next search when the list is tightened; installing
  one of them is refused.
