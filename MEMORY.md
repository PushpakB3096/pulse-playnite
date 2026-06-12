# Agent memory (PlayLog)

Instructions for AI assistants working in this repo.

## Follow explicit instructions

Never substitute your own judgment when the user has given a clear instruction (branch name, base branch, scope, approach, version number, etc.). Do what they asked.

If you see a problem, conflict, or a seemingly better option, **stop and tell the user** before changing direction. Ask or explain; do not silently pick a different path.

Example: if they say branch from the current branch and open a PR against it, do not rebase onto `master` or change the PR base without their approval.
