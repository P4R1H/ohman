#!/usr/bin/env python3
# fake-mangohud.py: stands in for a game with MangoHud loaded, for testing Ohman's benchmark where no game can
# run (WSL, CI). It is NOT MangoHud. It imitates the parts Ohman talks to, written from MangoHud's source
# (master 2bfd645): the abstract control socket (src/overlay_params.cpp parse_control, src/control.cpp) and the
# log (src/logging.cpp). What it does, as MangoHud does it:
#   * listens on the abstract socket "mangohud-<pid>" (control=mangohud-%p), one client at a time, and serves it
#     once a "frame";
#   * greets a client with :MangoHudControlVersion=1;:DeviceName=...;:MangoHudVersion=...;
#   * :logging=1; starts a log, :logging=0; stops it, :logging; toggles; a stop is answered with :LoggingFinished
#   * a log is <output_folder>/<program>_<YYYY-MM-DD_HH-MM-SS>.csv ($HOME by default): the system lines, the
#     header, then one row a frame, flushed as written; stopping writes <...>_summary.csv beside it.
#
#   fake-mangohud.py [--fps 87] [--seconds 120] [--output-folder DIR] [--name NAME] [--interval MS] [--versioning]
#                    [--autostart] [--no-control]
# --interval MS logs a sample every MS milliseconds instead of every frame (log_interval), --autostart has a log
# running before anybody connects (the owner pressed Shift+F2), --no-control opens no socket.
import argparse, os, random, socket, sys, time

ap = argparse.ArgumentParser()
ap.add_argument('--fps', type=float, default=87)
ap.add_argument('--seconds', type=float, default=120)
ap.add_argument('--output-folder', default=os.environ.get('HOME', '/tmp'))
ap.add_argument('--name', default='vkcube')
ap.add_argument('--interval', type=int, default=0)
ap.add_argument('--versioning', action='store_true')
ap.add_argument('--autostart', action='store_true')
ap.add_argument('--no-control', action='store_true')
a = ap.parse_args()

listener = None
if not a.no_control:
    listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    listener.bind('\0mangohud-%d' % os.getpid())
    listener.listen(1)
    listener.setblocking(False)
client = None
buf = b''
log = None
log_path = None
log_start = 0
last_logged = 0
rows = []
rnd = random.Random(7)

def start_logging():
    global log, log_path, log_start, rows
    if log: return
    log_path = os.path.join(a.output_folder, a.name + '_' + time.strftime('%Y-%m-%d_%H-%M-%S') + '.csv')
    log = open(log_path, 'a')
    log_start = time.monotonic_ns()
    rows = []
    if a.versioning:
        log.write('v1\n0.8.4\n---------------------SYSTEM INFO---------------------\n')
    log.write('os,cpu,gpu,ram,kernel,driver,cpuscheduler\n')
    log.write('Ubuntu 22.04.4 LTS,AMD Ryzen 7 7840HS w/ Radeon 780M Graphics,NVIDIA GeForce RTX 4060 Laptop GPU,15601140,6.8.0-45-generic,4.6.0 NVIDIA 550.107.02,schedutil\n')
    if a.versioning:
        log.write('--------------------FRAME METRICS--------------------\n')
    log.write('fps,frametime,cpu_load,cpu_power,gpu_load,cpu_temp,gpu_temp,gpu_core_clock,gpu_mem_clock,gpu_vram_used,gpu_power,ram_used,swap_used,process_rss,cpu_mhz,elapsed\n')
    log.flush()
    print('fake-mangohud: logging to', log_path, flush=True)

def stop_logging():
    global log
    if not log: return
    log.close(); log = None
    with open(log_path[:-4] + '_summary.csv', 'a') as s:
        s.write('0.1% Min FPS,1% Min FPS,97% Percentile FPS,Average FPS,GPU Load,CPU Load,Average Frame Time\n')
        s.write('0,0,0,%.1f,97,23,%.1f\n' % (1000 * len(rows) / max(1e-9, sum(rows)), sum(rows) / max(1, len(rows))))
    if client:
        try: client.send(b':LoggingFinished')        # control_send with no parameter writes no semicolon
        except OSError: pass
    print('fake-mangohud: log stopped after', len(rows), 'rows', flush=True)

def command(cmd, param):
    if cmd == 'logging':
        if param:
            if int(param) == 0 and log: stop_logging()
            elif int(param) > 0 and not log: start_logging()
        else:
            stop_logging() if log else start_logging()

def control():
    global client, buf
    if listener is None: return
    if client is None:
        try:
            client, _ = listener.accept()
            client.setblocking(False)
            client.send(b':MangoHudControlVersion=1;:DeviceName=NVIDIA GeForce RTX 4060 Laptop GPU;:MangoHudVersion=MangoHud 0.8.4;')
        except BlockingIOError: return
    try:
        data = client.recv(4096)
    except BlockingIOError: return
    except OSError: data = b''
    if not data:
        client.close(); client = None; buf = b''
        return
    buf += data
    while b';' in buf:
        one, buf = buf.split(b';', 1)
        if b':' not in one: continue
        one = one[one.rindex(b':') + 1:]
        cmd, _, param = one.partition(b'=')
        command(cmd.decode(), param.decode())

if a.autostart: start_logging()
print('fake-mangohud: pid', os.getpid(), 'socket', 'none' if listener is None else '@mangohud-%d' % os.getpid(), flush=True)
end = time.monotonic() + a.seconds
last = time.monotonic_ns()
base = 1000.0 / a.fps
while time.monotonic() < end:
    ms = base + (rnd.random() + rnd.random() + rnd.random() - 1.5) * base * 0.14 + (30 if rnd.random() < 0.004 else 0)
    time.sleep(ms / 1000.0)
    now = time.monotonic_ns()
    frametime = (now - last) / 1e6
    last = now
    control()
    if log and (a.interval == 0 or now - last_logged >= a.interval * 1000000):
        last_logged = now
        rows.append(frametime)
        log.write('%g,%g,23.4,0,97,71,68,1890,7000,4.31,96,9.52,0,3.1,4012,%d\n' % (1000 / frametime, frametime, now - log_start))
        log.flush()
stop_logging()
