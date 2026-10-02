# TaskMaster

A Jellyfin plugin that runs your scheduled tasks **sequentially** — one at
a time, in the order you choose, inside a time window you control.

Jellyfin normally fires scheduled tasks on their own independent timers.
On a busy server that means library scans, metadata refreshes, subtitle
downloads, and trickplay generation can all start at once and fight for
CPU, disk, and network. TaskMaster takes over the schedule: it disables
the native triggers, imports the tasks into a queue, and runs them one
after another in the order you configure.

## Features

- **Sequential execution** — tasks run one at a time, never overlapping
- **Smart ordering** — fastest-first or slowest-first, based on measured
  completion times (learned from Jellyfin's own history and TaskMaster's
  own runs), or fully manual with drag-and-drop reordering
- **Time window** — the sequence only runs between configured start and
  end times; overnight windows are supported
- **Per-day exclusions** — a task can run on Mondays and Wednesdays only,
  or every day, or never
- **Take over / Release** — one click backs up every task's native
  triggers, then disables them so only TaskMaster controls the schedule.
  Startup triggers are preserved so plugins still initialize on boot.
  Release restores everything from the backup.
- **Backup / Restore / Download** — snapshots of every task's triggers
  are stored as plain JSON, downloadable for safekeeping
- **Discovery** — new tasks added by other plugins show up in a
  "Discovered" section with a warning banner; import them with one click
- **Auto-import** (optional) — automatically add any new tasks to the
  sequence at the start of every run, so you never have to remember to
  import them manually
- **Manual-run override** — click "Run now" to run the sequence outside
  the configured time window, with a confirmation prompt
- **Stop button** — halts the sequence after the current task finishes

## Requirements

- Jellyfin **12.1** or later
- Server running .NET 10 (Jellyfin 12.x uses this by default)

## Installation

### From the plugin repository

1. Open Jellyfin **Dashboard → Plugins → Repositories**.
2. Click **Add**.
3. Name it `TaskMaster` and paste this URL: https://raw.githubusercontent.com/jnracreates/taskmaster/main/manifest.json
4. Save, then go to **Dashboard → Plugins → Catalog**.
5. Find **TaskMaster** and click **Install**.
6. Restart Jellyfin.

### Manual install

1. Download `taskmaster_x.y.z.w.zip` from the
[Releases](https://github.com/jnracreates/jellyfin-plugin-taskmaster/releases) page.
2. Extract it into your Jellyfin plugins folder. The exact path depends
on your install:
- **Docker:** `{config}/plugins/TaskMaster_x.y.z.w/`
- **Linux (native):** `~/.local/share/jellyfin/plugins/TaskMaster_x.y.z.w/`
- **Windows:** `%LOCALAPPDATA%\jellyfin\plugins\TaskMaster_x.y.z.w\`
3. Restart Jellyfin.

## First-time setup

Once installed, open **Dashboard → Plugins → TaskMaster** to configure it.

### 1. Import your tasks

The first time you open the page, every existing Jellyfin task appears in
the **Discovered tasks** section. Click **Import all** to add them to
TaskMaster's list, or select individual ones and click **Import selected**.

Tasks Jellyfin had scheduled come in enabled. Tasks with no native
trigger (one-shot migrations and manual-only utilities) come in
**disabled** — you can tick them individually if you want them in the
sequence.

### 2. Set your ordering

Under **Scheduling**, pick one:

- **Fastest first** — quick tasks first, long ones last. Good default.
- **Slowest first** — start with the long ones.
- **Manual** — use the order you set by dragging task cards around.

Averages are learned automatically. On the first run, unknown tasks are
placed at the end so they don't crowd out the ones with known durations.
After 2–3 runs, every task has its own measured average.

### 3. Set a time window

Enter a start and end time (e.g. `02:00` to `06:00`). If end is earlier
than start, it's treated as an overnight window. Leave both blank for no
limit.

### 4. Take over

Click **Take over**. This:

1. Backs up every task's current triggers to disk.
2. Imports any scheduled tasks that weren't in TaskMaster's list.
3. Disables each task's `Daily`, `Weekly`, and `Interval` triggers so
Jellyfin doesn't fire them independently.
4. Leaves `Startup` triggers alone so plugins still initialize on boot.

To undo this at any time, click **Release** — it restores the latest
backup.

### 5. Set TaskMaster's own trigger

Go to **Dashboard → Scheduled Tasks**, find **"TaskMaster: Run ordered
task sequence"**, and set when you want the whole pipeline to start. The
default is daily at 02:00, matching the default time window.

## Auto-import

Under **Scheduling**, there's an **Auto-import new tasks** checkbox. Off
by default.

**When off:**

- New tasks appear in the yellow "Discovered" banner on the config page.
- TaskMaster logs a warning to the Jellyfin log at the start of each run.
- You review and click **Import all** (or **Import selected**) when ready.

**When on:**

- Any Jellyfin task not yet in TaskMaster's list is automatically added at
the start of every orchestrator run.
- Tasks Jellyfin had scheduled come in **enabled** with no day
restrictions.
- Tasks with no native trigger (manual-only utilities and one-shot
migrations) come in **disabled**, so they don't run by accident.
- No warning is logged; the import happens silently.

**Which to pick:**

- **Off** if you want to review new tasks before they run. When you
install a plugin that adds a two-hour "Analyze All Media" task, you'll
see it in the Discovered list before it runs overnight.
- **On** if you'd rather not think about it. New tasks get added and slot
into the sequence automatically — new tasks sort to the end on their
first night (unknown duration), then move to their proper position
once their runtime is measured.

Because Take over preserves startup triggers but disables scheduled ones,
Auto-import is the way new scheduled tasks find their way into the
sequence after the initial setup.

## How the ordering learns

TaskMaster keeps a rolling average of the last 20 measured runtimes for
each task. Sources, in priority order:

1. **TaskMaster's own samples** — most accurate, measured directly.
2. **Jellyfin's last execution result** — what the Scheduled Tasks page
shows as "last run duration". Populated on day one.
3. **Jellyfin's on-disk history files** — older Jellyfin versions wrote
these; 12.x stores results in the database instead.

Task names you've never run sort last (unknown durations) so they don't
displace tasks whose speed you actually know.

## Controls on the config page

| Button | What it does |
|---|---|
| **Run now** | Runs the sequence immediately. If you're outside the time window, asks for confirmation first. |
| **Stop sequence** | Halts the sequence after the current task finishes. |
| **Preview sequence** | Shows the exact order that would run today, with average times. |
| **Backup now** | Snapshots every task's current triggers to a JSON file. |
| **Take over** | Backs up, imports, and disables scheduled triggers. |
| **Release** | Restores native triggers from the latest backup. |

## Backups

Backups are written to:
```bash
{DataPath}/taskmaster/backups/backup-YYYYMMDD-HHMMSS.json
```

Each file is a plain JSON snapshot of every task's triggers at the
moment the backup was taken. You can:

- **Download** any backup from the config page for off-server storage.
- **Restore** any backup to roll back trigger changes.
- **Prune** old backups via the automatic 20-file retention (older
  files are deleted as new ones are created).

## Troubleshooting

**TaskMaster ran a task I don't want.**
Untick its Enabled checkbox on the task card and save.

**A task didn't run at all.**
Check whether it's ticked, whether today is in its day list, and whether
the sequence stopped early because the time window ended. The Jellyfin
log shows a "past end of time window" line if that's what happened.

**A new plugin's task didn't show up.**
Open the TaskMaster config page, if the yellow "new tasks detected"
banner appears, click **Import selected** or **Import all**. For hands-off
handling, enable **Auto-import new tasks** under Scheduling (see
[Auto-import](#auto-import) above).

**Jellyfin stopped firing a task at boot.**
Take over preserves startup triggers, so this shouldn't happen. If it
does, click **Release** to restore the backup and check the log for
errors related to that task.

**The sequence keeps getting cut off.**
Widen the time window, or clear the end time entirely for "no limit."

## Building from source

```bash
git clone https://github.com/jnracreates/jellyfin-plugin-taskmaster.git
cd jellyfin-plugin-taskmaster
dotnet build -c Release
```
Output is in bin/Release/net10.0/. Copy the DLL and logo.png into a
TaskMaster_1.0.0.0/ folder under your Jellyfin plugins directory.

## License

MIT — see LICENSE.
Contributing

Issues and pull requests welcome. Please open an issue first for
non-trivial changes so we can discuss the approach.


Not affiliated with the Taskmaster television series.
