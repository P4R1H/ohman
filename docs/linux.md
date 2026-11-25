# Ohman on Linux

> **Status: not verified on real hardware under Linux.** The Linux build is the same engine, profiles and
> firmware commands as the Windows one, which owners have verified on their laptops. What reaches the
> hardware on Linux (a small kernel module and a root helper) is new, and nobody has run it on an OMEN or a
> Victus yet. Expect rough edges. **Testers wanted:** install it, run `ohman-probe`, and
> [open an issue](https://github.com/P4R1H/Ohman/issues) with what it printed, whether it worked or not.

## Install

```
curl -fsSL https://github.com/P4R1H/Ohman/releases/latest/download/ohman-install.sh | sudo bash
```

That downloads the latest release, checks it against the checksum published beside it, unpacks it into a
temporary directory and runs its `install.sh`. If build packages are missing it stops, changes nothing, and
prints the one command that installs them. To let it do that itself:

```
curl -fsSL https://github.com/P4R1H/Ohman/releases/latest/download/ohman-install.sh | sudo bash -s -- --install-deps
```

If you would rather read before you run, which is reasonable for something that installs a kernel module:
download `ohman-<version>-linux-x64.tar.gz` from [Releases](https://github.com/P4R1H/Ohman/releases/latest),
then

```
tar -xzf ohman-*-linux-x64.tar.gz
cd ohman-*-linux-x64
./install.sh --check        # looks, reports, changes nothing, needs no root
sudo ./install.sh
```

When it finishes it lists what is left for you to do. Usually two things:

1. **Log out and back in.** You were added to the group `ohman`, and group membership only counts for
   sessions started afterwards.
2. **Reboot and enrol a key**, only if Secure Boot is on. See [Secure Boot](#secure-boot).

Then start Ohman from the applications menu, or run `ohman`.

Running the installer again upgrades: it stops the helper, replaces the files, rebuilds the kernel module
only if its source changed, and starts the helper again. Ohman on Linux does not replace its own files the
way the Windows build does; a new version is the install command run again.

| `install.sh` | |
|---|---|
| `--check` | report what is present and what is missing; change nothing |
| `--install-deps` | install missing build packages with the distribution's package manager |
| `--no-module` | everything except the kernel module (Ohman starts, but cannot reach the firmware) |
| `--user NAME` | add this user to the group instead of whoever ran `sudo` |
| `--sign` / `--no-sign` | force module signing on or off, when the installer's guess about your kernel is wrong |
| `--uninstall` | the same as `uninstall.sh` |
| `--root DIR` | install into `DIR` as if it were `/` and only print the commands that would change the system |

## What gets installed, and why

Ohman talks to HP's firmware through a BIOS mailbox, the same one OMEN Gaming Hub uses on Windows. On Linux
only the kernel can reach it, and only root may ask the kernel. So there are three pieces:

| | where | what it is for |
|---|---|---|
| `ohman` | `/opt/ohman`, `/usr/bin/ohman` | The window. Runs as you, holds your settings and all the logic. |
| `ohman-helper` | `/opt/ohman`, `ohman-helper.service` | A small root service. It forwards hardware access for the window (mailbox calls, embedded-controller bytes, CPU registers, the OMEN key) over `/run/ohman/helper.sock` and decides nothing itself. It runs under a systemd sandbox: one capability, three device classes, a read-only file system, no network. The unit file says why for each line. |
| `ohman-wmi` | `/usr/src/ohman-wmi-<version>`, built by DKMS | A small kernel module. It passes mailbox calls and embedded-controller bytes through (`/dev/ohman-wmi`, root only) and knows nothing about what a command means. DKMS rebuilds it for every new kernel. |
| `ohman-probe` | `/opt/ohman`, `/usr/bin/ohman-probe` | A console program that prints what Ohman makes of this laptop. For support requests. |

And the glue:

- **Group `ohman`.** The helper's socket belongs to `root:ohman`, mode 0660. Whoever is in the group can
  drive the fans and the firmware settings Ohman exposes; nobody else can. The installer adds the user who
  ran `sudo`. Membership of the group `ohman` is direct access to HP's firmware mailbox, the same access
  Ohman itself has, so add only users you would trust with the fan and power settings of the machine.
- **`/etc/udev/rules.d/70-ohman.rules`.** Per-key keyboards are lit through `/dev/hidraw*`, which is
  root-only by default. The rule gives the keyboard controllers Ohman knows to the group.
- **`/etc/modules-load.d/ohman-wmi.conf`.** Loads `ohman-wmi` and the kernel's own `msr` module at boot.
- **`/usr/share/applications/ohman.desktop`** and an icon.
- **`/opt/ohman/uninstall.sh`**, so removing it needs nothing but what is installed, and a copy of
  `install.sh` beside it that can only `--check`.

Your own files are in the usual places: settings in `~/.config/ohman/ohman.state`, the log in
`~/.local/state/ohman/ohman.log`, benchmark runs in `~/.local/share/ohman/`.

**The kernel's own `hp-wmi` driver is left alone.** It stays loaded, it is not blacklisted, and it keeps
doing what it does (hotkeys, rfkill, `platform_profile`). The firmware serialises single calls, so the two
cannot corrupt each other. See [hp-wmi and the fans](#hp-wmi-is-fighting-ohman-over-the-fans) for the one
place they can disagree.

The programs carry their own .NET runtime, one copy shared by the three in `/opt/ohman`; you do not need
.NET installed. That is also why the download is about 39 MB when the Windows one is 1.6.

## Secure Boot

With Secure Boot on, Fedora, Debian, Ubuntu and openSUSE lock the kernel down: it only loads modules signed
by a key the firmware trusts. A module built on your machine is signed by a key that exists only on your
machine, so that key has to be enrolled once. (Arch does not lock down by default; there the installer says
so and skips all of this.)

What the installer does:

1. DKMS builds the module and signs it with the machine's own key. DKMS 3 keeps one in `/var/lib/dkms`;
   Ubuntu and Debian keep theirs in `/var/lib/shim-signed/mok`. If you have enrolled that key before, for
   the NVIDIA driver or VirtualBox for instance, **there is nothing more to do** and no reboot.
2. If the DKMS on your distribution does not sign (DKMS 2), the installer makes a key in
   `/var/lib/ohman/mok/` (readable by root only, marked as valid for kernel modules and nothing else) and
   signs the module with that, now and after every kernel update.
3. If the key is not enrolled yet, it runs `mokutil --import`. **`mokutil` asks you to choose a password.**
   Anything will do: it is used once. The installer never sees, chooses or stores it.
4. **Reboot.** Before the system starts, a blue screen titled *Perform MOK management* appears. Press a key
   within ten seconds, then: **Enroll MOK → Continue → Yes →** type the password **→ Reboot**. The keyboard
   layout on that screen is US.

If you miss the ten seconds, nothing is enrolled and the request is gone: run the installer again.

The installer does not change DKMS's signing configuration and never points other modules at a new key.

To see where things stand later: `/opt/ohman/install.sh --check`, or by hand

```
mokutil --sb-state                         # is Secure Boot on
modinfo -F signer ohman-wmi                # who signed the module (empty: unsigned)
mokutil --list-enrolled | grep -i -A1 CN=  # which keys the firmware trusts
mokutil --list-new                         # keys waiting for the reboot
```

The other way out is to switch Secure Boot off in the firmware setup (F10 at power-on on HP laptops). Then
no key is needed.

## Supported distributions and kernels

The installer knows the package names for these families and has the Secure Boot handling above for them:

| family | includes | needs |
|---|---|---|
| Debian | Debian 12+, Ubuntu 22.04+, Mint, Pop!_OS | `dkms make gcc linux-headers-$(uname -r)`; with Secure Boot `mokutil openssl` |
| Fedora | Fedora 39+ | `dkms make gcc kernel-devel-$(uname -r)`; with Secure Boot `mokutil openssl` |
| Arch | Arch, EndeavourOS, CachyOS, Manjaro | `dkms make gcc` and the headers of your kernel (`linux-headers`, `linux-lts-headers`, ...) |
| openSUSE | Tumbleweed, Leap 15.6 | `dkms make gcc kernel-default-devel`; with Secure Boot `mokutil openssl` |

"Knows" is the honest word: each of these was written from the distribution's documentation, and **none has
been run end to end on a real install yet.** On anything else the installer still works if `dkms`, a
compiler and your kernel's headers are present; it tells you what it could not find.

- **x86-64 only**, systemd only.
- **Kernel 5.15 or newer** is what the module is written for. It uses nothing newer than the kernel's WMI
  and misc-device interfaces. Reports from older and from very new kernels are welcome.
- **Image-based systems are refused.** Fedora Silverblue and Kinoite, Bazzite, SteamOS and the like keep
  `/usr` read-only and have no working DKMS, so the installer stops before changing anything and says so.
  There is no supported way to run Ohman on them yet.
- **The window** needs X11 or XWayland, `fontconfig` and the usual X libraries (`libX11`, `libICE`, `libSM`).
  Every desktop install has them.

## What works, what differs, what is missing

The engine is one copy of the code for both systems, so what Ohman *decides* for a laptop is the same on
both: its modes, fan tables, limits, the thermal guard, the lighting protocol. `ohman-probe` shows that
decision. What differs is everything around it.

**Meant to work as on Windows** (and waiting for somebody to confirm it on a laptop): modes, fan modes and
curves, max fan, power gain, GPU power, graphics switching (with the reboot it needs), keyboard lighting in
one zone, four zones and per key, live temperatures, power and fan speeds, the OMEN key, the thermal guard,
the tray menu, the on-screen flash, start at login, and the same window, page for page.

**Different on Linux**

- **Hotkeys work on X11 only.** A Wayland session gives a program no way to take a key combination
  globally, so the Hotkeys row says so and the shortcuts do nothing there. The OMEN key is not affected: it
  comes from the firmware through the helper.
- **The tray icon needs a desktop that shows tray icons.** KDE, Cinnamon, XFCE and most others do. GNOME
  needs the AppIndicator extension; without it there is no icon, and starting Ohman again brings the window
  back.
- **The benchmark measures through [MangoHud](https://github.com/flightlessmango/MangoHud)** instead of
  PresentMon. The game has to be started with MangoHud and its control socket; the Benchmark page gives the
  launch option to copy when it finds no such game. See [benchmark.md](benchmark.md).
- **"Sync the system power profile"** follows the mode through `tuned` (`tuned-adm profile`, preferred) or
  `power-profiles-daemon` (`powerprofilesctl`), where Windows has its power-mode slider. The row is hidden when
  neither is installed. The sync is both ways: change the profile in the desktop's own settings (or run
  `tuned-adm` yourself) while Ohman runs and the mode follows -- through D-Bus signals and a watch on tuned's
  own file, events, never a timer.
- **Refresh rate** is switched through `xrandr`, so the row is there on X11 and hidden on Wayland.
- No PawnIO. What the optional driver does on Windows (the CPU's own registers, the embedded controller)
  goes through the kernel instead: `/dev/cpu/*/msr` and the `ohman-wmi` module, both via the helper. Ohman
  cannot install or update them from its window; the installer does.
- Under Secure Boot the kernel refuses writes to CPU registers. Reading still works; raising the package
  power limit through the register does not. The firmware's own power controls are not affected.
- Settings, log and benchmark runs are in your home directory (above), not beside the program.
- Nothing asks for a password while Ohman runs. Being in the group `ohman` is the permission.
- Ohman tells you when a newer version is out; updating is running the installer again.
- Removing is `uninstall.sh`. Reset in Settings clears Ohman's own settings and hands the fans back.

**Not there, because it is about Windows:** stopping OMEN Gaming Hub's key handler and tasks, Windows
Dynamic Lighting, starting without a UAC prompt.

**Not seen on a real desktop yet.** The window was built and compared against the Windows one picture by
picture, and its controls were exercised by an automated test, all without a screen. How it behaves under
your window manager (placement next to the tray, the page-change animation, staying on top for the flash
and the benchmark pill) is exactly the kind of report that helps.

If a control is missing or greyed out on your laptop under Linux and present on Windows, that is worth an
issue.

## Verifying a laptop on Linux

```
ohman-probe
```

prints the board id, what the firmware answers to the read-only queries, and the block "Ohman's decision":
the profile the engine built for this machine. It changes nothing on the laptop. On simulated hardware,
which works anywhere, including a virtual machine:

```
ohman-probe --demo --board 8A25
ohman --demo
```

For a report, attach the output of `ohman-probe`, of `/opt/ohman/install.sh --check`, and
`~/.local/state/ohman/ohman.log`. If your board is already verified on Windows
([docs/laptops.md](laptops.md)), say whether the same controls behave the same; that is the comparison
that matters. The checklist in [docs/laptops.md](laptops.md#verifying-your-laptop) applies unchanged.

## Troubleshooting

Start with `/opt/ohman/install.sh --check` (no root needed). It names most of what follows.

### The module is not loaded

```
lsmod | grep ohman_wmi
sudo modprobe ohman-wmi
sudo dmesg | tail
```

| `modprobe` says | meaning |
|---|---|
| `Key was rejected by service`, `Required key not available`, `Operation not permitted` | Secure Boot: the module's key is not enrolled. Next section. |
| `No such device` | The module loaded, found no HP BIOS mailbox, and left again. This is not an OMEN or Victus, or the kernel's WMI support is missing. `dmesg` has the line `ohman_wmi: no HP BIOS mailbox on this machine`. |
| `Module ohman-wmi not found` | DKMS has not built it for this kernel. `dkms status` shows which kernels it has. Install the headers for the running kernel (the table above) and run `sudo dkms autoinstall`, or the installer again. This is what a kernel update without its headers package looks like. |
| `Exec format error` | Built for another kernel. `sudo dkms install ohman-wmi/<version> -k $(uname -r) --force`. |

After loading the module by hand, restart the helper: `sudo systemctl restart ohman-helper`. Its sandbox
fixes which devices it may open when it starts.

### The key is not enrolled

The check says "its key is NOT enrolled" or "waits for the enrolment reboot". Waiting: reboot
and go through the blue screen ([Secure Boot](#secure-boot), step 4). Not enrolled: run the installer
again, or import by hand with the path `--check` printed:

```
sudo mokutil --import /var/lib/dkms/mok.pub
```

`mokutil` saying `EFI variables are not supported` means the system was not booted through shim (a custom
boot chain, or legacy BIOS mode); then either the kernel is not locked down and `--no-sign` is right, or you
sign modules by your own means and know more about your set-up than this page does.

### The helper is not running

```
systemctl status ohman-helper
journalctl -u ohman-helper -b
```

- `status=217/GROUP`: the group `ohman` does not exist. Run the installer again.
- `Operation not permitted` on `/dev/ohman-wmi` in the log: the module was loaded after the helper started.
  `sudo systemctl restart ohman-helper`.
- It starts and the window still says it cannot reach it: see the next section.

To rule the sandbox out, run the helper by hand for a moment:
`sudo systemctl stop ohman-helper; sudo /opt/ohman/ohman-helper`. If that works and the service does not,
that is a bug in the unit file and worth an issue with the journal lines.

### You are not in the group

```
id -nG | tr ' ' '\n' | grep ohman      # this session
id -nG "$USER" | grep ohman            # the user database
```

In the second and not the first: you have not logged out since the install. A new terminal is not enough,
the whole session has to end; a reboot does it. In neither: `sudo usermod -aG ohman "$USER"`, then log out.

### hp-wmi is fighting Ohman over the fans

The kernel's `hp-wmi` driver can control the fans too, through `pwm1_enable` in
`/sys/devices/platform/hp-wmi/hwmon/hwmon*/`. If something else on the system sets it to manual or full
speed (a fan-control tool, `fancontrol`, a script, another OMEN utility), it and Ohman will take turns
overruling each other: fans that jump between two speeds every few seconds are this.

```
cat /sys/devices/platform/hp-wmi/hwmon/hwmon*/pwm1_enable    # 2 = automatic, 0 = full speed
```

Ohman expects `2` and puts it back. Run one fan controller, not two: if omen-fan, `nbfc`, `fancontrol` or a
CoolerControl profile is driving these fans, stop it while Ohman runs.

`power-profiles-daemon` and `tuned` are a milder case. They do not touch the fans, but the performance mode
they set through `platform_profile` and the mode you pick in Ohman are the same firmware setting, and the
last one to write wins. With "Sync the system power profile" on, the two move together: Ohman writes the
profile when its mode changes, and a profile changed elsewhere -- the desktop's own settings, `tuned-adm` in
a terminal -- moves Ohman's mode with it. If modes seem to change by themselves, that is where to look.

### The keyboard does not light, or only as root

Per-key keyboards need the udev rule to match. `ls -l /dev/hidraw*` should show group `ohman` on at least
one node. If none does, find your keyboard's ids with `ls -l /sys/class/hidraw/*/device` (the name ends in
`VENDOR:PRODUCT.number`), add a line for it to `/etc/udev/rules.d/70-ohman.rules` as the file describes, and
tell us the ids so the next release has them.

### The window draws slowly

The window is drawn by the processor, not the graphics card: a GL context would load the whole 3D driver
into a program that mostly sits in the tray, at a cost of 100 to 250 MB of memory. If animations stutter on
your machine, `OHMAN_GPU=1 ohman` draws through the graphics card instead; tell us if that was needed.

## Uninstall

```
sudo /opt/ohman/uninstall.sh
```

It asks a running Ohman to quit, puts the fans back in automatic, stops and removes the service, removes
the module from DKMS for every kernel, and deletes the files, the udev rule, the desktop entry and the
group. It leaves two things and says so:

- your settings, log and benchmark runs in your home directory;
- the signing key in `/var/lib/ohman/mok`, if one was made, so a reinstall needs no new enrolment.

`sudo /opt/ohman/uninstall.sh --purge` removes those as well, and asks the firmware to forget the key (one
more password and one more blue screen). A key that DKMS or the distribution owns is never touched: other
modules on your machine are signed with it.

## Building it yourself

```
linux/packaging/publish.sh       # needs the .NET 8 SDK; writes linux/.build/publish/
linux/packaging/publish.sh --single-file   # one large file per program instead of a shared runtime
```

makes the same tarball a release carries. `CONTRIBUTING.md` has the rules for the code the two systems
share.

After changing `install.sh` or `uninstall.sh`, run them for real without root and without touching your
machine:

```
linux/packaging/test-real.sh ohman-1.3.5-linux-x64      # an unpacked release
```

It runs both scripts in their real mode inside a user namespace, with stand-ins for `dkms`, `systemctl`,
`modprobe`, `mokutil` and the rest, and checks the resulting file tree after install, upgrade, failures,
Secure Boot signing and uninstall.
