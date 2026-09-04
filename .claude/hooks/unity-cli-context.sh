#!/usr/bin/env bash
# SessionStart hook: the `unity` CLI is the interface to this Unity project.
# Emits the live editor state plus the actual command inventory from the running
# editor's Pipeline server, so the listing stays accurate as the package changes.
set -uo pipefail

status_json="$(unity status --format json 2>/dev/null || true)"

if printf '%s' "$status_json" | grep -q '"port"'; then
  ports="$(printf '%s' "$status_json" | grep -o '"port": *[0-9]*' | grep -o '[0-9]*' | paste -sd, -)"
  live="A Unity editor is LIVE and reachable right now (port ${ports:-unknown})."
  inventory="$(unity command --detail compact --format json 2>/dev/null \
    | python -c "
import json,sys
try:
    cmds = json.load(sys.stdin)['data']['commands']
except Exception:
    sys.exit(1)
tags = {}
for c in cmds:
    for t in (c.get('tags') or ['untagged']):
        tags.setdefault(t, 0)
        tags[t] += 1
print('%d commands are available on the live editor, by tag:' % len(cmds))
print('  ' + '  '.join('%s(%d)' % (t, n) for t, n in sorted(tags.items())))
" 2>/dev/null)"
else
  live="No Unity editor is reachable via \`unity status\` right now. Start the editor (or use \`unity run\` for headless batch) before reaching for anything else."
  inventory=""
fi

export CONTEXT
read -r -d '' CONTEXT <<CTX || true
THE \`unity\` CLI IS THE INTERFACE TO THIS UNITY PROJECT. ${live}

\`unity command <name>\` drives the running editor through its Pipeline server. It covers far more
than logs: assets, prefabs, GameObjects and components, scenes, materials and shaders, animation and
Timeline, lighting/navmesh/occlusion baking, build settings and targets, packages, project settings,
screen capture, tests, play mode, script compilation and C# eval.

${inventory}

DISCOVER BEFORE YOU IMPROVISE. Whenever a task touches the editor, first search the inventory:
  unity command --query <term> --detail compact --format json   # substring match on name/desc/tag
  unity command --tag <tag> --detail compact --format json      # one tag subtree, e.g. prefabs
  unity command --format json                                   # full listing WITH parameter schemas
If a command exists for the job, use it. Only fall back to editing files directly when nothing fits.

DO NOT, when a \`unity command\` exists for it:
  - grep Editor.log, AssetImportWorker*.log, or Library/ to learn what the editor is doing
  - hand-edit .unity / .prefab / .asset / ProjectSettings YAML
  - write a throwaway [MenuItem] or -executeMethod script to poke the editor
  - ask the user to click through the editor UI or read the console back to you

Frequently needed:
  unity status                                    # running editors, port, state
  unity command get_console_logs --level Error    # read compile/runtime errors
  unity command clear_console
  unity command recompile ; unity command recompile_status
  unity editor play | stop | pause
  unity eval '<C# expression>'                    # evaluate in the live editor
  unity run <project> -- -executeMethod X.Y       # headless batch, no editor needed

After editing C#: clear_console -> recompile -> poll recompile_status until completed ->
get_console_logs --level Error. Report the actual counts, not an assumption.

Every command takes --format json. The \`unity-cli\` skill has the full reference for the
non-\`command\` groups (install, editors, projects, templates, build, doctor, cache, auth).
CTX

python -c "
import json,os
print(json.dumps({'hookSpecificOutput':{'hookEventName':'SessionStart','additionalContext':os.environ['CONTEXT']}}))
" 2>/dev/null || printf '{}'
