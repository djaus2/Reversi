# Blazor Online Reversi (Othello) game with text and TTS Voice chat

> The Reversi game played between 2 players over the internet. Lobby Text chat and in-game Text and Voice Chat. All players must be in the same group/s to be able to play each other, See Home page for app for details. Note that a player can only be logged in once at a time. Note also that there are a number of timeouts for the app.
>
> **Latest:** AI as the second player! Levels: Random / Easy / Moderate / Hard (3-ply lookahead)  
> Also added animated flipping.

> **Chat and voice:** lobby and in-game text chat, speech recognition, TTS and the voice floor come from the shared [GameVoice](https://github.com/djaus2/GameVoice) package on [nuget.org](https://www.nuget.org/packages/GameVoice); see [docs/game-voice-library.md](docs/game-voice-library.md).

Try it at [https://reversi.sportronics.com.au](https://reversi.sportronics.com.au)

Rules: standard Reversi/Othello on an 8x8 board. Black moves first. A move places a disc so that
one or more lines of opponent discs are bracketed and flipped. If a player has no legal move they
must pass; the game ends when neither player can move, and the winner holds the most discs.

AI levels:

| Level | Strategy |
|-------|----------|
| Random | Any legal move |
| Easy | Random, but avoids giving you a corner |
| Moderate | Best immediate positional gain (1-ply) |
| Hard | 3-ply lookahead with positional evaluation |

See [docs/AI-Player-Implementation.md](docs/AI-Player-Implementation.md) for details and [docs/index.md](docs/index.md) for the rest of the documentation.

## Build and run

Requires the .NET 10 SDK.

```text
dotnet build
dotnet run --launch-profile http
```

Then open <http://localhost:5146.> Seeded users are defined in `auth.json`.

Forked from [djaus2/Drafts](https://github.com/djaus2/Drafts) — a remote 2-player Draughts Blazor service game.

Has been developed through AI-Coding specifications. Copious documentation generated as well.

Further: [Blog Posts](https://davidjones.sportronics.com.au/cats/ai/)
