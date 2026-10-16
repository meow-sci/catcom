# Reference material

Folder /Users/asherwin/repos/meow-sci/ksa-game-assemblies contains decompiled KSA kitten space agency game code

The ksa-modding skill contains detailed information about how various mods have done things in KSA

The info on gatOS it the most pertinent here for us

The folder /Users/asherwin/repos/meow-sci/ksa-linux  is available which is a full game copy with a Content/Core/ folder which contains assets and shaders etc

/Users/asherwin/repos/meow-sci/gatOS contains the gatOS code

/Users/asherwin/repos/meow-sci/purrtty contains the purrTTY mod code

# Your task

gatOS is a linux sidecar VM with a in-game purrTTY terminal emulator bridge and can control the game via a serialization abstraction but it's out of band so it can't properly participate with custom code and the game loop

i want a solution which will use purrTTY for in-band terminal emulator, but run a native shell on the host (which purrTTY can already do), but I want integrate with a new solution

i want in-process / in-band KSA scripting which which provide a similar data reading and writing pattern of controlling the game like gatOS does in theory, but done in-band

i want to explore options like using Lua via LuaJIT and TypeScript by looking at the v8 runtime (i dont want a transpilation step for TS to JS on the end-user, so either this needs to be v8 which can do it natively OR a different JS runtime and we can use vite8+ tooling to do the transpilation transparently and run in some other JS runtime)

do deep research into the docs/research folder as a valid okf bundle, be thorough and exhaustive and find what our best options are for integrating something like this with KSA

ultimately i want the end user to use a TUI like nvim and have a full editing experience in there, we could ship platform native copies of nvim and make it "just work" in a purrTTY terminal that launches straight into the shipped nvim for the given host platform (windows or linux), and when the user saves the edits, it will be a script we have registered in-game 

now these scripts could be used different ways, i am thinking of having different game hooks where you could have like a script you run ad-hoc that can access APIs that will do things in the game (read/write data, control things like we do with gatOS)

but other ways would be to like write a functional hook entrypoint that the mod could then run in the game loop during certain events, for example, "run this script whenever the engine of any craft ignites", so they would be a form of event based hooks to run, and they could be done sync or async in the mod code which handles it

do deep research, be exhaustive, look into the KSA decompiled sources and our purrTTY and gatOS mods, and figure out how we would properly link in LuaJIT and a JS runtime like v8, but another well established and still supported JS runtime would be acceptable as long as we could transparently (from an end-user perspective) run typescript code

put all your findings on research on what the options are and how to hook them into KSA as a mod into a docs/research okf bundle so future agentic ai coding sessions can leverage all the knowledge you distill and we dont lose it after this session ends, be detailed and exhaustive

make plans for the implementations for each scripting runtime under plans/ (do a separate plan for each runtime proposal)