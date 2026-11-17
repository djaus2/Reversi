# AI Player Implementation

## Overview

The Reversi game includes an AI opponent, so a player can play solo games against the computer at one of four difficulty levels. This document describes the current implementation in `Services/AiService.cs`.

## Difficulty levels

All four levels are implemented. The level is chosen on the Player page and stored as an integer:
`0` = Easy, `1` = Moderate, `2` = Hard, `3` = Random.

| Level | Strategy |
|-------|----------|
| Random | Picks any legal move at random. No evaluation. |
| Easy | Picks a random move, but avoids moves that give the opponent a legal move onto a corner. Falls back to any legal move if every move gives up a corner. |
| Moderate | Greedy 1-ply: plays the move whose resulting board has the best evaluation score. |
| Hard | 3-ply lookahead: AI move, then the opponent's best reply, then the AI's best counter. The AI picks the move with the best worst-case outcome (minimax, no pruning). |

### Evaluation function

Moderate and Hard score a board from the AI's point of view using:

- **Positional weights:** a classic 8x8 weight table. Corners are worth `+120`, the squares next to corners (X- and C-squares) are negative, and edges are positive.
- **Mobility:** `10 x (AI legal moves - opponent legal moves)`.
- **Disc difference:** `(AI discs - opponent discs)`, weighted more heavily as the board fills up.

In Hard mode, a line where the opponent must pass scores `+50`, and a line where the AI must pass scores `-50`.

## How AI mode works

### Game setup
1. The player ticks **Play against AI** on the Player page.
2. They choose a difficulty level from the dropdown.
3. They click **Start a new game**. The AI joins automatically.
4. The player is always Black (player 1) and moves first. The AI is White (player 2).

### Game flow
1. **Player's turn:** the player clicks one of the highlighted legal squares to place a disc. All bracketed White discs are flipped.
2. **AI's turn:** after a short delay, `AiService.MakeRandomMove` picks a move for the chosen level and submits it through `ReversiService.MakeMove`, the same validation path human moves use.
3. **Passes:** if the side to move has no legal move, it passes automatically and a system chat message is shown.
4. **Game end:** the game ends when neither side can move. The winner is the side with more discs; equal counts are a draw.

### Algorithms

**Random**
```
1. List all legal moves for the AI
2. Pick one at random
```

**Easy**
```
1. List all legal moves for the AI
2. For each move, simulate it and check whether the opponent could then play on a corner
3. Pick randomly from the moves that do not give up a corner (or from all moves if none are safe)
```

**Moderate**
```
1. List all legal moves for the AI
2. For each move, simulate it and evaluate the resulting board
3. Play the highest-scoring move
```

**Hard**
```
1. For each AI move (ply 1), simulate it
2. For each opponent reply (ply 2), simulate it
3. For each AI counter (ply 3), evaluate the board; keep the best counter
4. The opponent is assumed to choose the reply that minimises that score
5. Play the AI move with the highest resulting score
```

## User interface

- The Player page shows the chosen level and a one-line description of it.
- In game, a "Playing against AI" banner shows the difficulty level.
- AI moves are logged with the difficulty level.
- Chat and voice features are hidden in AI games.

## Technical architecture

- **AiService:** move selection for every difficulty level. It works on a copy of the board, so simulations never change the live game.
- **ReversiService:** game state, legal-move generation, flipping, passes and end-of-game detection.
- **Integration:** AiService chooses a move; ReversiService validates and applies it.

### Performance

Measured in a headless run of 160 AI-vs-AI games: the slowest AI move (Hard) took about 75 ms. Hard beat Moderate and Random in all games played, and beat Easy in most.

## Future enhancements

- Alpha-beta pruning and deeper search for Hard
- Opening book and exact endgame solving for the last few empty squares
- Stability and frontier-disc terms in the evaluation
- AI analysis / hint mode showing the AI's suggested move
- AI vs AI spectator mode

## Testing

- Automated AI-vs-AI simulation of all level pairings: legal moves only, correct disc counts after every move, correct pass handling, and the correct winner.
- Manual browser play-testing of Random and Hard games.
