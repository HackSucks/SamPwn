#!/usr/bin/gawk -f
# SamPwn boot hook.
#
# This file replaces /opt/etc/parseinfo.sh on the TV.  AT-HUB.service runs it
# as root on every boot via:  ExecStart=/opt/etc/parseinfo.sh &
#
# bash and node are patched by Samsung's UEP check and refuse to run unsigned
# scripts.  gawk is not patched, so a shebang of "#!/usr/bin/gawk -f" lets this
# unsigned script execute.  Commands are run through gawk's system(), which
# uses an inline "sh -c" string (inline strings are not UEP-checked).
#
# The payloads run as transient systemd units created under /run/systemd/system
# so they are not killed when this short-lived AT-HUB service exits.
#
# Safety:
#   * AT-HUB.service has no dependents and nothing waits for it to exit.
#   * sampwn.service is bounded to 3 start attempts per 60 s, then stops.
#   * sampwn-ui.service retries the launcher up to 20 times over ~1 min and
#     only runs when the widget is actually installed.
#   * A failure anywhere in this file can only mean "no root shell"; it does
#     not gate any boot target.

BEGIN {
    # ------------------------------------------------------------------
    # 1. Preserve the original parseinfo.sh behaviour (/proc/target_version).
    #    Done through system() so a failed write cannot abort this script.
    # ------------------------------------------------------------------
    repo = ""
    model = ""
    while ((getline line < "/etc/prd_info.ini") > 0) {
        if (line ~ /^Build=/) {
            n = split(line, a, "=")
            split(a[n], b, ";")
            repo = b[1]
        }
        if (line ~ /^Model=/) {
            n = split(line, a, "=")
            split(a[n], b, ";")
            model = b[1]
        }
    }
    close("/etc/prd_info.ini")
    gsub(/[^A-Za-z0-9._-]/, "", repo)
    gsub(/[^A-Za-z0-9._-]/, "", model)
    system("/bin/printf '__" repo "__" model "\\n' > /proc/target_version 2>/dev/null")

    # ------------------------------------------------------------------
    # 2. Operator kill switch.  If /opt/etc/sampwn/disabled exists, do the
    #    original parseinfo job and nothing else.  This lets a user turn the
    #    boot payload off without restoring the original file.
    # ------------------------------------------------------------------
    if (system("/bin/test -e /opt/etc/sampwn/disabled") == 0) {
        exit
    }

    # ------------------------------------------------------------------
    # 3. SamPwn control + root-shell daemon.  The daemon always serves a
    #    localhost-only control API on 127.0.0.1:2324.  It opens the LAN
    #    root shell on 0.0.0.0:2323 only when shell.state is on (default).
    #    /run is tmpfs, so the unit is recreated on every boot.
    # ------------------------------------------------------------------
    unit = "/run/systemd/system/sampwn.service"
    print "[Unit]"                                            > unit
    print "Description=SamPwn control and root shell"         > unit
    print "ConditionPathExists=/opt/etc/sampwn/SampwnSrv.dll" > unit
    print "StartLimitIntervalSec=60"                          > unit
    print "StartLimitBurst=3"                                 > unit
    print ""                                                  > unit
    print "[Service]"                                         > unit
    print "Type=simple"                                       > unit
    print "ExecStart=/usr/bin/dotnet /opt/etc/sampwn/SampwnSrv.dll" > unit
    print "Restart=on-failure"                                > unit
    print "RestartSec=3"                                      > unit
    print "TimeoutStopSec=5"                                  > unit
    close(unit)

    # ------------------------------------------------------------------
    # 4. Launch the SamPwn widget.  index.html decides between the dashboard
    #    and the one-shot pwned.html show.
    #    NOTE: systemd expands $VAR in ExecStart, so this command must not
    #    contain any shell variables.  A literal for-list is used instead.
    # ------------------------------------------------------------------
    unit = "/run/systemd/system/sampwn-ui.service"
    print "[Unit]"                                            > unit
    print "Description=SamPwn dashboard and show"             > unit
    print "ConditionPathExists=/opt/usr/apps/sampwnroot/res/wgt/index.html" > unit
    print "StartLimitIntervalSec=120"                         > unit
    print "StartLimitBurst=2"                                 > unit
    print ""                                                  > unit
    print "[Service]"                                         > unit
    print "Type=oneshot"                                      > unit
    print "TimeoutStartSec=600"                               > unit
    print "Environment=XDG_RUNTIME_DIR=/run/user/5001"        > unit
    print "Environment=\"DBUS_SESSION_BUS_ADDRESS=kernel:path=/sys/fs/kdbus/5001-user/bus;unix:path=/run/user/5001/dbus/user_bus_socket\"" > unit
    print "ExecStartPre=/bin/sleep 5"                         > unit
    print "ExecStart=/bin/sh -c 'for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do /usr/bin/app_launcher --start sampwnroot.dashboard >/dev/null 2>&1 && exit 0; /bin/sleep 3; done; exit 1'" > unit
    close(unit)

    # ------------------------------------------------------------------
    # 5. Queue the one-shot show, then start the units.  systemd 244 loads
    #    brand-new unit files from /run/systemd/system on demand, so no
    #    daemon-reload is needed (and avoiding it removes any early-boot
    #    reload race).
    # ------------------------------------------------------------------
    # A persistent no-show marker suppresses the automatic one-shot show.
    # The dashboard button still works; remove the marker to restore autoplay.
    if (system("/bin/test -e /opt/etc/sampwn/no-show") != 0) {
        system("/bin/touch /opt/etc/sampwn/show.pending 2>/dev/null")
    }
    system("/bin/chmod 644 /run/systemd/system/sampwn.service /run/systemd/system/sampwn-ui.service 2>/dev/null")
    system("/bin/systemctl start --no-block sampwn.service sampwn-ui.service >/dev/null 2>&1")

    exit
}
