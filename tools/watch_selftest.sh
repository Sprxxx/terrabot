#!/bin/bash
# Streams the interesting lines of the newest tModLoader client log while a self-test runs.
# Reports STALL when the log has not grown for 60 s while the game runs (the self-test logs a heartbeat
# every 30 game seconds, so silence means a frozen game), and GAME_EXITED when tModLoader is gone.
# Usage: tools/watch_selftest.sh ["extended regex of lines to show"]
L="/c/Program Files (x86)/Steam/steamapps/common/tModLoader/tModLoader-Logs"
PATTERN="${1:-SELFTEST|Auto: |Build (start|done)|failed|stuck|Exception|ERROR|Died|Boss fight: (Eye|boss|lost)|Planner: }"
# tModLoader itself, not any dotnet process (the build server and test hosts are dotnet too).
game_running() {
  powershell -NoProfile -Command "if (Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | Where-Object { \$_.CommandLine -like '*tModLoader.dll*' -and \$_.CommandLine -notlike '*-build*' }) { 'yes' }" 2>/dev/null | grep -q yes
}
sleep 30
file=""; seen=0; last_change=$(date +%s); stalled=0
while true; do
  f=$(ls -t "$L"/client*.log | head -1)
  n=$(wc -l < "$f")
  # A new run starts a new log (another name, or client.log rotated and shorter): read it from the top.
  if [ "$f" != "$file" ] || [ "$n" -lt "$seen" ]; then file=$f; seen=0; fi
  if [ "$n" -gt "$seen" ]; then
    sed -n "$((seen+1)),${n}p" "$f" | grep -E "$PATTERN" | cut -c1-230 | head -25
    seen=$n; last_change=$(date +%s); stalled=0
  elif [ $(( $(date +%s) - last_change )) -ge 60 ] && [ $stalled -eq 0 ]; then
    echo "STALL: no log line for 60 s (last: $(tail -1 "$f" | cut -c1-160))"
    stalled=1
  fi
  game_running || { echo GAME_EXITED; break; }
  sleep 10
done
