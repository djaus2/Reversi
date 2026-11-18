using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Reversi.Services;

public class AiService
{
    private readonly ReversiService _reversiService;
    private readonly ILogger<AiService> _logger;

    private readonly Random _rng = new();

    public AiService(ReversiService reversiService, ILogger<AiService> logger)
    {
        _reversiService = reversiService;
        _logger = logger;
    }

    // Difficulty levels: 0=Easy, 1=Moderate, 2=Hard, 3=Random
    public (bool success, string? message, int? r, int? c) SelectMove(string gameId, int aiPlayer, int difficultyLevel = 1)
    {
        var game = _reversiService.GetGame(gameId);
        if (game == null) return (false, "Game not found", null, null);

        if (game.State != ReversiService.GameState.Connected && game.State != ReversiService.GameState.Playing)
        {
            return (false, "Game is not in a playable state", null, null);
        }

        if (game.CurrentTurn != aiPlayer)
        {
            return (false, "Not AI's turn", null, null);
        }

        Move? selected;
        lock (game)
        {
            var legalMoves = GetLegalMoves(game.Board, aiPlayer);
            if (legalMoves.Count == 0)
            {
                selected = null;
            }
            else
            {
                selected = difficultyLevel switch
                {
                    0 => SelectEasyMove(game.Board, legalMoves, aiPlayer),
                    1 => SelectModerateMove(game.Board, legalMoves, aiPlayer),
                    2 => SelectHardMove(game.Board, legalMoves, aiPlayer),
                    _ => legalMoves[_rng.Next(legalMoves.Count)]
                };
            }
        }

        if (selected is null)
        {
            return (false, "No moves available", null, null);
        }

        return (true, null, selected.R, selected.C);
    }

    // Difficulty levels: 0=Easy, 1=Moderate, 2=Hard, 3=Random
    public (bool success, string? message, int? r, int? c) MakeRandomMove(string gameId, int aiPlayer, int difficultyLevel = 1)
    {
        var (selectedOk, selectedMessage, r, c) = SelectMove(gameId, aiPlayer, difficultyLevel);
        if (!selectedOk || !r.HasValue || !c.HasValue)
        {
            return (false, selectedMessage ?? "No moves available", null, null);
        }

        var (moveSuccess, moveMessage) = _reversiService.MakeMove(gameId, aiPlayer, r.Value, c.Value);

        if (moveSuccess)
        {
            _logger.LogInformation("AI (difficulty {Difficulty}) placed disc at {R},{C}",
                difficultyLevel, r.Value, c.Value);
            return (true, null, r.Value, c.Value);
        }
        else
        {
            return (false, moveMessage ?? "Failed to place disc", null, null);
        }
    }

    private sealed record Move(int R, int C);

    private static readonly (int dr, int dc)[] Directions =
    {
        (-1, -1), (-1, 0), (-1, 1),
        (0, -1),           (0, 1),
        (1, -1),  (1, 0),  (1, 1)
    };

    // Classic positional weights: corners are valuable, the squares next to a
    // corner are dangerous while the corner is empty, edges are good.
    private static readonly int[,] SquareWeights =
    {
        { 120, -20,  20,   5,   5,  20, -20, 120 },
        { -20, -40,  -5,  -5,  -5,  -5, -40, -20 },
        {  20,  -5,  15,   3,   3,  15,  -5,  20 },
        {   5,  -5,   3,   3,   3,   3,  -5,   5 },
        {   5,  -5,   3,   3,   3,   3,  -5,   5 },
        {  20,  -5,  15,   3,   3,  15,  -5,  20 },
        { -20, -40,  -5,  -5,  -5,  -5, -40, -20 },
        { 120, -20,  20,   5,   5,  20, -20, 120 }
    };

    // Easy: random move, but avoid moves that let the opponent take a corner.
    private Move SelectEasyMove(int[,] board, List<Move> legalMoves, int aiPlayer)
    {
        var opponent = 3 - aiPlayer;
        var safeMoves = new List<Move>();

        foreach (var move in legalMoves)
        {
            var next = ApplyMove(board, aiPlayer, move);
            var givesCorner = GetLegalMoves(next, opponent).Any(m => IsCorner(m.R, m.C));
            if (!givesCorner)
            {
                safeMoves.Add(move);
            }
        }

        var pool = safeMoves.Count > 0 ? safeMoves : legalMoves;
        return pool[_rng.Next(pool.Count)];
    }

    // Moderate: greedy 1-ply - maximise immediate positional gain.
    private Move SelectModerateMove(int[,] board, List<Move> legalMoves, int aiPlayer)
    {
        var best = legalMoves[0];
        var bestScore = int.MinValue;

        foreach (var move in legalMoves)
        {
            var score = Evaluate(ApplyMove(board, aiPlayer, move), aiPlayer);
            if (score > bestScore)
            {
                bestScore = score;
                best = move;
            }
        }

        return best;
    }

    // Hard: 3-ply lookahead (AI move, opponent's best reply, AI's best counter).
    private Move SelectHardMove(int[,] board, List<Move> legalMoves, int aiPlayer)
    {
        var opponent = 3 - aiPlayer;
        var best = legalMoves[0];
        var bestScore = int.MinValue;

        foreach (var move in legalMoves)
        {
            var next = ApplyMove(board, aiPlayer, move);

            int score;
            var opponentMoves = GetLegalMoves(next, opponent);
            if (opponentMoves.Count == 0)
            {
                // Opponent must pass: treat as a strong position.
                score = Evaluate(next, aiPlayer) + 50;
            }
            else
            {
                // Opponent picks their best reply (worst case for us).
                var worst = int.MaxValue;
                foreach (var om in opponentMoves)
                {
                    var afterOpp = ApplyMove(next, opponent, om);

                    var ourReplies = GetLegalMoves(afterOpp, aiPlayer);
                    int leafScore;
                    if (ourReplies.Count == 0)
                    {
                        leafScore = Evaluate(afterOpp, aiPlayer) - 50;
                    }
                    else
                    {
                        var bestReply = int.MinValue;
                        foreach (var reply in ourReplies)
                        {
                            var s = Evaluate(ApplyMove(afterOpp, aiPlayer, reply), aiPlayer);
                            if (s > bestReply) bestReply = s;
                        }
                        leafScore = bestReply;
                    }

                    if (leafScore < worst) worst = leafScore;
                }
                score = worst;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = move;
            }
        }

        return best;
    }

    private int Evaluate(int[,] board, int aiPlayer)
    {
        var opponent = 3 - aiPlayer;
        var aiDiscs = 0;
        var oppDiscs = 0;
        var score = 0;

        for (var r = 0; r < 8; r++)
        {
            for (var c = 0; c < 8; c++)
            {
                var p = board[r, c];
                if (p == aiPlayer)
                {
                    aiDiscs++;
                    score += SquareWeights[r, c];
                }
                else if (p == opponent)
                {
                    oppDiscs++;
                    score -= SquareWeights[r, c];
                }
            }
        }

        // Mobility: having more options than the opponent is worth a lot.
        score += 10 * (GetLegalMoves(board, aiPlayer).Count - GetLegalMoves(board, opponent).Count);

        // Disc differential matters more later in the game.
        var total = aiDiscs + oppDiscs;
        score += (aiDiscs - oppDiscs) * Math.Max(1, (total - 4) / 16);

        return score;
    }

    private static bool IsCorner(int r, int c) =>
        (r == 0 || r == 7) && (c == 0 || c == 7);

    private List<Move> GetLegalMoves(int[,] board, int player)
    {
        var moves = new List<Move>();
        for (var r = 0; r < 8; r++)
        {
            for (var c = 0; c < 8; c++)
            {
                if (board[r, c] == 0 && GetFlipsForMove(board, player, r, c).Count > 0)
                {
                    moves.Add(new Move(r, c));
                }
            }
        }
        return moves;
    }

    private static List<(int R, int C)> GetFlipsForMove(int[,] board, int player, int r, int c)
    {
        var flips = new List<(int R, int C)>();
        if (!IsInside(r, c) || board[r, c] != 0) return flips;

        var opponent = 3 - player;

        foreach (var (dr, dc) in Directions)
        {
            var run = new List<(int R, int C)>();
            var rr = r + dr;
            var cc = c + dc;

            while (IsInside(rr, cc) && board[rr, cc] == opponent)
            {
                run.Add((rr, cc));
                rr += dr;
                cc += dc;
            }

            if (run.Count > 0 && IsInside(rr, cc) && board[rr, cc] == player)
            {
                flips.AddRange(run);
            }
        }

        return flips;
    }

    private static int[,] ApplyMove(int[,] board, int player, Move move)
    {
        var next = (int[,])board.Clone();
        next[move.R, move.C] = player;
        foreach (var p in GetFlipsForMove(board, player, move.R, move.C))
        {
            next[p.R, p.C] = player;
        }
        return next;
    }

    private static bool IsInside(int r, int c) => r >= 0 && r < 8 && c >= 0 && c < 8;
}
