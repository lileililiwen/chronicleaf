# Chronicleaf agent instructions

Read README.md, requirement.md, ROADMAP.md, HANDOFF.md and applicable .ai-rules files. Preserve the complete brief, MVP boundary and unrelated edits. Work on main; no branch/worktree without designated purpose. React web UI is primary.

OpenSpec is the durable planning authority. Run `node scripts/check-openspec-change-names.mjs`, then `openspec list`; select one dependency-ready change and set exactly one `current_spec` in HANDOFF.md. Use BFS → DFS → BFS and local verification before strict validation/archive; never use --skip-specs.

Each completed OpenSpec spec/change requires exactly two commits: first the implementation/tests/archive commit, then a HANDOFF.md-only pointer/evidence commit.

Create both commits without conversational confirmation when implementation is authorized, then stop; do not push. Advance current_spec after archive or remove it if no change remains. Read .ai-rules/workflow.md and .ai-rules/completion.md. Skeleton/build/planning success is not delivered MVP. Treat credentials and repository evidence as private by default.
