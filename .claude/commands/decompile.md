Decompile and document parts of the Slay the Spire 2 game assembly (`sts2.dll`).

## Purpose
Incrementally reverse-engineer the game's internals and record findings in `docs/decompiled/`. Each discovery is versioned against the game build it was observed in.

## Setup
1. Locate `sts2.dll` via the build script or common Steam paths (check `build.ps1` for game directory resolution logic).
2. Ensure `ilspycmd` is available: `dotnet tool install -g ilspycmd` if not installed.
3. Read the index at `docs/decompiled/README.md` to see what's already been documented.

## Workflow

When the user asks to decompile a namespace, class, or feature:

1. **Decompile** the target using `ilspycmd`:
   - List types: `ilspycmd <dll> -l`
   - Decompile a type: `ilspycmd <dll> -t <FullTypeName>`
   - Decompile a namespace: `ilspycmd <dll> -t <Namespace>.*` (or decompile key types individually)

2. **Analyze** the decompiled code:
   - Identify public API surface (methods, properties, events)
   - Note inheritance hierarchies and interfaces
   - Map relationships to other game types
   - Flag anything useful for modding (virtual methods, singletons, debug methods)

3. **Document** findings into a markdown file under `docs/decompiled/`:
   - One file per logical area (e.g., `multiplayer.md`, `combat.md`, `entities.md`)
   - Use the format specified below
   - Update the index at `docs/decompiled/README.md`

4. **Summarize** key takeaways for the user — what's interesting, what's moddable, what's risky.

## Documentation Format

Each doc file in `docs/decompiled/` should follow this structure:

```markdown
# <Area Name>

> Game version: `v0.99.1` | Last updated: 2026-03-28

## Overview
Brief description of what this area covers and why it matters for modding.

## Key Types

### `Namespace.ClassName`
- **Role**: What this class does
- **Singleton?**: Yes/No (and how to access)
- **Key Properties**: List important properties with types
- **Key Methods**: List important methods with signatures and behavior notes
- **Modding Notes**: Virtual methods, debug methods, injection points

## Relationships
How these types connect to other documented areas.

## Modding Implications
What can be patched, extended, or called from mods. Risks and fragility notes.
```

## Rules
- Always record the game version (`v0.99.1` currently) — findings may break across updates.
- Don't dump raw decompiled code into docs. Summarize and annotate.
- Focus on **modding-relevant** details: public API, singletons, debug methods, virtual/override points, action queues.
- Cross-reference other decompiled docs when types span areas.
- If a type has already been documented, update it rather than creating a duplicate.
- Note when something is speculative vs. confirmed through testing.
