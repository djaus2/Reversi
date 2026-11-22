# Chess Design Thoughts

Design notes (no implementation yet) for building a Chess app in the same style as the Blazor Server [Draughts](https://github.com/djaus2/Drafts), [Reversi](https://github.com/djaus2/Reversi) and [WalkieTalkie](https://github.com/djaus2/walkietalkie) apps.

Chess fits the shell these apps already share. Most of the new work is the rules engine, a stronger AI and the board UI.

## Where to start

- Fork **Drafts**, not Reversi. Draughts already uses from→to moves (`MakeMove(id, player, fr, fc, tr, tc)`), click a piece then click a target, and last-move from/to highlights. Chess needs the same.
- Keep the rest unchanged: login, groups, lobby chat, voice, idle/game timeouts, game log, admin pages and the Azure deploy. In Reversi, most of the 1,485-line `ReversiService` is this kind of shared code, not game rules.
- This would be the fourth copy of that shell. Consider moving it into a shared Razor class library, with each game supplying only its rules, AI and board component. This is optional, but every later fix would then reach all the apps.

## Game state

- The current `int[,]` holding 0/1/2 isn't enough. Use signed piece codes (+1…+6 for White pawn through king, negative for Black) and also track:
  - side to move
  - castling rights
  - en passant square
  - half-move clock (for the 50-move rule)
  - position history (for threefold repetition)
- Use [FEN](https://en.wikipedia.org/wiki/Forsyth%E2%80%93Edwards_Notation), the standard one-line chess position string, for snapshots and admin "set up position".
- Use standard algebraic notation (e.g. `Nf3+`) and [PGN](https://en.wikipedia.org/wiki/Portable_Game_Notation), the standard game-record format, for the move list, game log and export.

## Rules

- Generate each piece's moves, then reject any move that leaves your own king in check. This one check covers pins, discovered checks and moving into check.
- Special cases:
  - castling, which can't start in, pass through or end in check
  - en passant
  - promotion, which needs a piece-picker in the UI
- Game end: checkmate, stalemate, 50-move rule, threefold repetition, insufficient material, resignation (the existing Concede) and a new **Offer draw** button.
- The Draughts-only logic goes: forced capture, multi-jump and the grace period.
- **Build or borrow:** a hand-written engine is about 600–1,000 lines of C#. There are also MIT-licensed .NET libraries, such as ChessDotNet; check whether they're still maintained.
- **Testing:** either way, verify with [perft](https://www.chessprogramming.org/Perft_Results), which counts every legal move sequence to a fixed depth. From the start position the counts are 20, 400, 8,902 and 197,281 for depths 1–4. Then reuse the headless AI-vs-AI harness used for Reversi.

## AI levels

Same four slots as now:

| Level | Approach |
|---|---|
| Random | Any legal move |
| Easy | 1-ply; take free material, avoid leaving pieces undefended |
| Moderate | 2–3 ply alpha-beta, material plus piece-square tables |
| Hard | Iterative deepening alpha-beta, 4–6 ply, best-looking moves searched first, quiescence search, time limit of about 1–2 s |

- Chess has about 35 legal moves per position, against roughly 10 in Reversi. Plain lookahead like Reversi's Hard (75 ms at 3-ply) won't scale, so the search needs alpha-beta pruning.
- On Blazor Server the search runs on the Azure CPU, so it needs a time limit, a cancel option and `Task.Run`, so it doesn't stall other users.
- For really strong play, run Stockfish either as a server process or in the browser via WebAssembly and JS interop. The WebAssembly route moves the CPU load off Azure.

## Board UI

- Show the board from Black's side for the Black player.
- Highlight legal targets after a piece is selected; this extends the existing `ListLegalMoves` idea. Also highlight the king when in check.
- Add a move list, a captured-pieces tray and a promotion dialog. Drag-and-drop would be optional, through JS interop.
- Use SVG pieces rather than Unicode ♔♚. The Unicode symbols render inconsistently on phones, and ♟ can turn into an emoji.

## Timing and persistence

- Chess games last much longer, so the max-game-time and idle defaults need raising.
- An optional chess clock (e.g. 10 min + 5 s per move) would be server-authoritative and enforced by the existing timeout reaper (`GameTimeoutReaper`).
- Games live only in memory, so an Azure app recycle drops them, and that hurts more in long games. Save FEN and PGN to SQLite after each move so games can resume.

## Voice tie-in

- Announce moves with the existing text-to-speech ("Knight to f3, check").
- Accept spoken moves ("e4", "knight f3") with the speech recognition WalkieTalkie already uses. Speech-to-text is fuzzy, so match it against the list of legal moves and confirm before playing.

## Effort

Probably one or two Devin sessions, mostly the engine and AI tuning. The perft counts make correctness easy to check.
