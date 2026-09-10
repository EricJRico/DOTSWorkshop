#!/usr/bin/env bash
# PreToolUse/Bash: refuse shell commands that write source files.
#
# Two things get blocked:
#   1. a write construct aimed at a .cs / .shader / .hlsl / .uxml / .uss path
#   2. a script piped or passed inline to an interpreter (python - <<EOF, perl -e, node -e)
#
# Both mean the same mistake: writing a script to write a file, when Edit and Write
# do it directly. Reading (cat, grep, sed -n, git diff) is untouched.

cmd=$(jq -r '.tool_input.command // empty')
[ -z "$cmd" ] && exit 0

deny() {
  jq -n --arg reason "$1" '{
    hookSpecificOutput: {
      hookEventName: "PreToolUse",
      permissionDecision: "deny",
      permissionDecisionReason: $reason
    }
  }'
  exit 0
}

# 1. Write construct aimed at a source file.
if printf '%s' "$cmd" | grep -Eqi '\.(cs|shader|hlsl|uxml|uss|asmdef)\b'; then
  if printf '%s' "$cmd" | grep -Eq '(>>?[[:space:]]*[^|&]*\.(cs|shader|hlsl|uxml|uss|asmdef)|sed[[:space:]]+[^|]*-i|tee[[:space:]]|Set-Content|Add-Content|Out-File)'; then
    deny "Bash cannot write source files here. Use the Edit tool on the file directly."
  fi
fi

# 2. A script fed to an interpreter.
if printf '%s' "$cmd" | grep -Eq '(^|[[:space:]|;&(])(python3?|perl|ruby|node)([[:space:]]+-[[:alnum:]]*)*[[:space:]]*(-[ce][[:space:]]|-[[:space:]]*$|-[[:space:]]*<)'; then
  deny "No inline interpreter scripts. If this was going to edit a file, use the Edit tool on that file instead."
fi

exit 0
