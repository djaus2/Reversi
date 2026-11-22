using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using GameVoice;
using Reversi.Data;

namespace Reversi.Services
{
    // Simple Reversi (Othello) game service for server-side interactive components.
    public class ReversiService : IVoiceRoomHost
    {
        private readonly ILogger<ReversiService> _logger;
        private readonly LobbyChatService _lobbyChat;
        private readonly SettingsService _settings;
        private readonly GameLogService _gameLog;

        // Event raised when a game changes. Subscribers can call StateHasChanged.
        public event Action<string>? GameUpdated;

        private readonly ConcurrentDictionary<string, ReversiGame> _games = new();

        private string? FindActiveGameIdForUser(int userId)
        {
            if (userId <= 0) return null;

            foreach (var kvp in _games)
            {
                var game = kvp.Value;
                if (game.CreatedByUserId == userId || game.Player1UserId == userId || game.Player2UserId == userId)
                {
                    return kvp.Key;
                }
            }

            return null;
        }

        public string? GetActiveGameIdForUser(int userId)
        {
            return FindActiveGameIdForUser(userId);
        }

        public ReversiService(ILogger<ReversiService> logger, LobbyChatService lobbyChat, SettingsService settings, GameLogService gameLog)
        {
            _logger = logger;
            _lobbyChat = lobbyChat;
            _settings = settings;
            _gameLog = gameLog;
        }

        public enum GameState
        {
            New,
            Connected,
            Playing,
            Finished,
            Abandoned
        }

        public sealed record GameListItem(
            string Id,
            DateTime CreatedUtc,
            DateTime StartTimeUtc,
            DateTime LastTimeUtc,
            int CreatedByUserId,
            int? Player1UserId,
            int? Player2UserId,
            bool Player1Connected,
            bool Player2Connected,
            bool HadSecondPlayerConnected,
            bool AdminMode,
            GameState State,
            int Player1PieceCount,
            int Player2PieceCount,
            int? GroupId);

        public List<GameListItem> ListGames()
        {
            // Snapshot view for UI.
            return _games.Values
                .Select(g => new GameListItem(
                    g.Id,
                    g.CreatedUtc,
                    g.StartTimeUtc,
                    g.LastTimeUtc,
                    g.CreatedByUserId,
                    g.Player1UserId,
                    g.Player2UserId,
                    g.Player1Connected,
                    g.Player2Connected,
                    g.HadSecondPlayerConnected,
                    g.AdminMode,
                    g.State,
                    g.Player1PieceCount,
                    g.Player2PieceCount,
                    g.GroupId))
                .ToList();
        }

        public string CreateGame(int userId, int creatorPlayerNumber = 1, bool adminMode = false, bool isAiGame = false)
        {
            return CreateGame(userId, "", creatorPlayerNumber, adminMode, null, isAiGame);
        }

        public string CreateGame(int userId, string creatorName, int creatorPlayerNumber = 1, bool adminMode = false, int? groupId = null, bool isAiGame = false)
        {
            var existing = FindActiveGameIdForUser(userId);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                _logger.LogInformation("CreateGame: user {UserId} already has active game {GameId}; returning existing", userId, existing);
                return existing;
            }

            var id = Guid.NewGuid().ToString("n").Substring(0, 8);
            var game = new ReversiGame(id)
            {
                CreatedByUserId = userId,
                GroupId = groupId,
                IsAiGame = isAiGame
            };

            game.AdminMode = adminMode;

            if (creatorPlayerNumber != 1 && creatorPlayerNumber != 2)
            {
                creatorPlayerNumber = 1;
            }

            if (creatorPlayerNumber == 1)
            {
                game.Player1Connected = true;
                game.Player1UserId = userId;
                game.Player1Name = creatorName ?? string.Empty;
            }
            else
            {
                game.Player2Connected = true;
                game.Player2UserId = userId;
                game.Player2Name = creatorName ?? string.Empty;
            }

            game.Touch();

            _games[id] = game;
            _logger.LogInformation("CreateGame: {GameId}", id);

            var displayName = string.IsNullOrWhiteSpace(creatorName) ? $"User {userId}" : creatorName;
            _lobbyChat.AddSystemMessage($"New game started by {displayName}: {id}", groupId);

            // Log game creation
            _ = _gameLog.LogGameEventAsync(LogType.GameCreated, userId, int.Parse(id, System.Globalization.NumberStyles.HexNumber), $"Game created by {displayName}", null, groupId);

            if (isAiGame)
            {
                var startDetails = $"Game started: initiator={displayName} (UserId={userId}), acceptor=AI";
                _ = _gameLog.LogGameEventAsync(LogType.GameStarted, userId, int.Parse(id, System.Globalization.NumberStyles.HexNumber), startDetails, null, groupId);
            }

            OnGameUpdated(id);
            return id;
        }

        public ReversiGame? GetGame(string id)
        {
            var ok = _games.TryGetValue(id, out var g);
            _logger.LogInformation("GetGame: {GameId} found={Found}", id, ok);
            return g;
        }

        // Returns 1 or 2 for player number, 0 if cannot join
        public int TryJoinGame(string id, int userId, string? userName)
        {
            var existing = FindActiveGameIdForUser(userId);
            if (!string.IsNullOrWhiteSpace(existing) && !string.Equals(existing, id, StringComparison.Ordinal))
            {
                _logger.LogWarning("TryJoinGame: user {UserId} already in game {ExistingGameId}; rejecting join to {GameId}", userId, existing, id);
                return -1;
            }

            var game = GetGame(id);
            if (game == null)
            {
                _logger.LogWarning("TryJoinGame: {GameId} not found", id);
                return 0;
            }

            lock (game)
            {
                if (game.Player1UserId == userId)
                {
                    if (string.IsNullOrWhiteSpace(game.Player1Name) && !string.IsNullOrWhiteSpace(userName))
                    {
                        game.Player1Name = userName;
                    }
                    return 1;
                }
                if (game.Player2UserId == userId)
                {
                    if (string.IsNullOrWhiteSpace(game.Player2Name) && !string.IsNullOrWhiteSpace(userName))
                    {
                        game.Player2Name = userName;
                    }
                    return 2;
                }

                if (game.Player1Connected && game.Player2Connected)
                {
                    _logger.LogWarning("TryJoinGame: {GameId} already full (p1={P1} p2={P2})", id, game.Player1Connected, game.Player2Connected);
                    return 0;
                }
                if (!game.Player1Connected)
                {
                    game.Player1Connected = true;
                    game.Player1UserId = userId;
                    game.Player1Name = userName ?? string.Empty;
                    var wasSecondPlayer = false;
                    if (game.Player2Connected)
                    {
                        game.HadSecondPlayerConnected = true;
                        wasSecondPlayer = true;
                        if (game.State == GameState.New)
                        {
                            game.State = GameState.Connected;
                        }
                    }

                    game.Touch();
                    _logger.LogInformation("TryJoinGame: {GameId} assigned Player1", id);
                    
                    if (wasSecondPlayer)
                    {
                        var p1Name = string.IsNullOrWhiteSpace(game.Player1Name) ? $"User {game.Player1UserId}" : game.Player1Name;
                        var p2Name = string.IsNullOrWhiteSpace(game.Player2Name) ? $"User {game.Player2UserId}" : game.Player2Name;
                        var details = $"Game started: initiator={p2Name} (UserId={game.Player2UserId}), acceptor={p1Name} (UserId={game.Player1UserId})";
                        _ = _gameLog.LogGameEventAsync(LogType.GameStarted, userId, int.Parse(id, System.Globalization.NumberStyles.HexNumber), details, null, game.GroupId);
                    }
                    else
                    {
                        _ = _gameLog.LogGameEventAsync(LogType.GameJoined, userId, int.Parse(id, System.Globalization.NumberStyles.HexNumber), $"Player joined as Player1 - {userName ?? $"User {userId}"}", null, game.GroupId);
                    }
                    
                    OnGameUpdated(id);
                    return 1;
                }
                if (!game.Player2Connected)
                {
                    game.Player2Connected = true;
                    game.Player2UserId = userId;
                    game.Player2Name = userName ?? string.Empty;
                    var wasSecondPlayer = false;
                    if (game.Player1Connected)
                    {
                        game.HadSecondPlayerConnected = true;
                        wasSecondPlayer = true;
                        if (game.State == GameState.New)
                        {
                            game.State = GameState.Connected;
                        }
                    }

                    game.Touch();
                    _logger.LogInformation("TryJoinGame: {GameId} assigned Player2", id);
                    _logger.LogInformation("Player2 joined game {GameId}", id);
                    
                    if (wasSecondPlayer)
                    {
                        var p1Name = string.IsNullOrWhiteSpace(game.Player1Name) ? $"User {game.Player1UserId}" : game.Player1Name;
                        var p2Name = string.IsNullOrWhiteSpace(game.Player2Name) ? $"User {game.Player2UserId}" : game.Player2Name;
                        var details = $"Game started: initiator={p1Name} (UserId={game.Player1UserId}), acceptor={p2Name} (UserId={game.Player2UserId})";
                        _ = _gameLog.LogGameEventAsync(LogType.GameStarted, userId, int.Parse(id, System.Globalization.NumberStyles.HexNumber), details, null, game.GroupId);
                    }
                    else
                    {
                        _ = _gameLog.LogGameEventAsync(LogType.GameJoined, userId, int.Parse(id, System.Globalization.NumberStyles.HexNumber), $"Player joined as Player2 - {userName ?? $"User {userId}"}", null, game.GroupId);
                    }
                    
                    OnGameUpdated(id);
                    return 2;
                }
                _logger.LogWarning("TryJoinGame: {GameId} unexpected state", id);
                return 0;
            }
        }

        public bool TryAnnounceVoiceInfo(string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId)) return false;

            var game = GetGame(gameId);
            if (game is null) return false;

            var did = false;
            lock (game)
            {
                if (game.VoiceInfoAnnounced) return false;
                if (!game.Player1Connected || !game.Player2Connected) return false;
                if (!game.Player1UserId.HasValue || !game.Player2UserId.HasValue) return false;

                var p1Id = game.Player1UserId.Value;
                var p2Id = game.Player2UserId.Value;

                var p1Name = string.IsNullOrWhiteSpace(game.Player1Name) ? $"User {p1Id}" : game.Player1Name;
                var p2Name = string.IsNullOrWhiteSpace(game.Player2Name) ? $"User {p2Id}" : game.Player2Name;

                var p1Voice = game.Voice.GetPreferredVoiceKey(p1Id);
                var p2Voice = game.Voice.GetPreferredVoiceKey(p2Id);

                var p1VoiceText = string.IsNullOrWhiteSpace(p1Voice) ? "(default voice)" : p1Voice;
                var p2VoiceText = string.IsNullOrWhiteSpace(p2Voice) ? "(default voice)" : p2Voice;

                game.ChatMessages.Add(new ReversiGame.ChatMessage(DateTime.UtcNow, 0, "System", $"{p1Name}: voice={p1VoiceText}"));
                game.ChatMessages.Add(new ReversiGame.ChatMessage(DateTime.UtcNow, 0, "System", $"{p2Name}: voice={p2VoiceText}"));
                game.VoiceInfoAnnounced = true;
                game.Touch();
                did = true;
            }

            if (did)
            {
                OnGameUpdated(gameId);
            }

            return did;
        }

        public int RemoveGamesForUser(int userId)
        {
            var removed = 0;
            foreach (var kvp in _games)
            {
                var game = kvp.Value;
                if (game.CreatedByUserId == userId || game.Player1UserId == userId || game.Player2UserId == userId)
                {
                    if (_games.TryRemove(kvp.Key, out _))
                    {
                        removed++;
                        _logger.LogInformation("RemoveGamesForUser: removed game {GameId} for user {UserId}", kvp.Key, userId);
                        OnGameUpdated(kvp.Key);
                    }
                }
            }

            return removed;
        }

        public bool RemoveGame(string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId)) return false;
            if (_games.TryRemove(gameId, out _))
            {
                _logger.LogInformation("RemoveGame: removed game {GameId}", gameId);
                OnGameUpdated(gameId);
                return true;
            }

            return false;
        }

        public bool AddChatMessage(string gameId, int senderUserId, string senderName, string text)
        {
            if (string.IsNullOrWhiteSpace(gameId)) return false;
            if (senderUserId < 0) return false;

            text = (text ?? string.Empty).Replace("\r\n", "\n").Trim();
            if (string.IsNullOrWhiteSpace(text)) return false;

            var senderDisplay = senderName ?? string.Empty;
            if (text.StartsWith("[VOICE] ", StringComparison.OrdinalIgnoreCase))
            {
                var payload = text.Substring("[VOICE] ".Length).Trim();
                _logger.LogInformation(
                    "VOICE chat received: game={GameId} senderId={SenderUserId} sender={SenderName} len={Len} text={Text}",
                    gameId,
                    senderUserId,
                    senderDisplay,
                    payload.Length,
                    payload);
            }

            var game = GetGame(gameId);
            if (game is null) return false;

            lock (game)
            {
                game.ChatMessages.Add(new ReversiGame.ChatMessage(DateTime.UtcNow, senderUserId, senderName ?? string.Empty, text));
                game.Touch();
            }

            OnGameUpdated(gameId);
            return true;
        }

        // Make a move: place a disc at (r, c) and flip all bracketed opponent discs.
        // Returns (success, message).
        public (bool success, string? message) MakeMove(string id, int player, int r, int c)
        {
            _logger.LogInformation("MakeMove: {GameId} player={Player} place at {R},{C}", id, player, r, c);
            var game = GetGame(id);
            if (game == null) return (false, "Game not found");
            lock (game)
            {
                if (game.State == GameState.Finished || game.State == GameState.Abandoned)
                {
                    return (false, $"Game is {game.State}");
                }

                if (game.State == GameState.New)
                {
                    return (false, "Waiting for second player");
                }

                game.Touch();

                if (game.CurrentTurn != player) return (false, "Not your turn");
                if (!IsInside(r, c)) return (false, "Out of bounds");
                if (game.Board[r, c] != 0) return (false, "Square already occupied");

                var flips = GetFlipsForMove(game.Board, player, r, c);
                if (flips.Count == 0) return (false, "Illegal move: placement must flip at least one disc");

                game.Board[r, c] = player;
                foreach (var p in flips)
                {
                    game.Board[p.R, p.C] = player;
                }

                game.LastMoveFromR = null;
                game.LastMoveFromC = null;
                game.LastMoveToR = r;
                game.LastMoveToC = c;
                game.LastMoveFlippedSquares.Clear();
                foreach (var p in flips)
                {
                    game.LastMoveFlippedSquares.Add(p);
                }

                if (game.State == GameState.Connected)
                {
                    game.State = GameState.Playing;
                }

                game.RecountPieces();
                ResolveTurnAfterMove(game, player);

                game.Touch();
                OnGameUpdated(id);
                _logger.LogInformation("MakeMove: {GameId} placed at {R},{C}; {FlipCount} disc(s) flipped", id, r, c, flips.Count);
                return (true, null);
            }
        }

        // After `player` moves: the opponent moves next if they have a legal move;
        // otherwise the opponent passes and `player` moves again; if neither can
        // move, the game is finished and the winner is decided by disc count.
        private void ResolveTurnAfterMove(ReversiGame game, int player)
        {
            var opponent = 3 - player;

            if (HasAnyLegalMove(game, opponent))
            {
                game.CurrentTurn = opponent;
                return;
            }

            if (HasAnyLegalMove(game, player))
            {
                game.CurrentTurn = player;

                var passerName = opponent == 1 ? game.Player1Name : game.Player2Name;
                var moverName = player == 1 ? game.Player1Name : game.Player2Name;
                var passer = string.IsNullOrWhiteSpace(passerName) ? $"Player {opponent}" : passerName;
                var mover = string.IsNullOrWhiteSpace(moverName) ? $"Player {player}" : moverName;
                game.ChatMessages.Add(new ReversiGame.ChatMessage(
                    DateTime.UtcNow,
                    0,
                    "System",
                    $"{passer} has no legal moves and must pass. {mover} to move again."));
                return;
            }

            MarkFinished(game);
        }

        private static readonly (int dr, int dc)[] Directions =
        {
            (-1, -1), (-1, 0), (-1, 1),
            (0, -1),           (0, 1),
            (1, -1),  (1, 0),  (1, 1)
        };

        // Returns the opponent discs that would be flipped by placing `player`'s
        // disc on empty square (r, c). An empty result means the move is illegal.
        private static List<ReversiGame.BoardPos> GetFlipsForMove(int[,] board, int player, int r, int c)
        {
            var flips = new List<ReversiGame.BoardPos>();
            if (!IsInside(r, c) || board[r, c] != 0) return flips;

            var opponent = 3 - player;

            foreach (var (dr, dc) in Directions)
            {
                var run = new List<ReversiGame.BoardPos>();
                var rr = r + dr;
                var cc = c + dc;

                while (IsInside(rr, cc) && board[rr, cc] == opponent)
                {
                    run.Add(new ReversiGame.BoardPos(rr, cc));
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

        public sealed record LegalMove(int R, int C);

        public IReadOnlyList<LegalMove> ListLegalMoves(string gameId, int player)
        {
            if (string.IsNullOrWhiteSpace(gameId)) return Array.Empty<LegalMove>();
            if (player != 1 && player != 2) return Array.Empty<LegalMove>();

            var game = GetGame(gameId);
            if (game is null) return Array.Empty<LegalMove>();

            lock (game)
            {
                if (game.State == GameState.Finished || game.State == GameState.Abandoned) return Array.Empty<LegalMove>();

                var list = new List<LegalMove>();
                for (var r = 0; r < 8; r++)
                {
                    for (var c = 0; c < 8; c++)
                    {
                        if (game.Board[r, c] == 0 && GetFlipsForMove(game.Board, player, r, c).Count > 0)
                        {
                            list.Add(new LegalMove(r, c));
                        }
                    }
                }

                return list;
            }
        }

        // Admin mode move. Places a disc for the side whose turn it is.
        public (bool success, string? message) MakeMoveAsAdmin(string id, int r, int c)
        {
            var game = GetGame(id);
            if (game == null) return (false, "Game not found");

            int actingPlayer;
            lock (game)
            {
                if (!game.AdminMode) return (false, "Not an admin-mode game");
                if (game.State == GameState.Finished || game.State == GameState.Abandoned)
                {
                    return (false, $"Game is {game.State}");
                }

                // Allow admin to start making moves immediately for testing.
                // (Normal games block moves in the New state until a second player joins.)
                if (game.State == GameState.New)
                {
                    game.State = GameState.Connected;
                }

                // Act as whoever's turn it is.
                actingPlayer = game.CurrentTurn;
            }

            // Call MakeMove outside the lock to avoid deadlocking on the same game lock.
            return MakeMove(id, actingPlayer, r, c);
        }

        // Admin mode delete (right-click). Deletes any disc at a cell.
        public (bool success, string? message) DeletePieceAsAdmin(string id, int r, int c)
        {
            var game = GetGame(id);
            if (game == null) return (false, "Game not found");

            lock (game)
            {
                if (!game.AdminMode) return (false, "Not an admin-mode game");
                if (game.State == GameState.Finished || game.State == GameState.Abandoned)
                {
                    return (false, $"Game is {game.State}");
                }

                if (!IsInside(r, c)) return (false, "Out of bounds");
                if (game.Board[r, c] == 0) return (false, "No disc");

                game.Board[r, c] = 0;
                game.RecountPieces();
                if (game.Player1PieceCount == 0 || game.Player2PieceCount == 0)
                {
                    MarkFinished(game);
                }

                game.Touch();
                OnGameUpdated(id);
                return (true, null);
            }
        }

        public void ClearLastMoveHighlight(string id)
        {
            var game = GetGame(id);
            if (game == null) return;

            lock (game)
            {
                game.LastMoveFromR = null;
                game.LastMoveFromC = null;
                game.LastMoveToR = null;
                game.LastMoveToC = null;
                game.Touch();
            }

            OnGameUpdated(id);
        }

        private void MarkFinished(ReversiGame game)
        {
            if (game.State == GameState.Finished)
            {
                return;
            }

            game.RecountPieces();

            // In Reversi the winner is whoever holds more discs; equal counts is a draw.
            var winner = 0;
            if (game.Player1PieceCount > game.Player2PieceCount) winner = 1;
            else if (game.Player2PieceCount > game.Player1PieceCount) winner = 2;

            if (winner != 0)
            {
                MarkFinishedWithWinner(game, winner, "disc-count");
                return;
            }

            game.State = GameState.Finished;

            if (!game.GameOverMessageSent)
            {
                game.GameOverMessageSent = true;
                var text = $"Game over. Draw {game.Player1PieceCount}-{game.Player2PieceCount}.";
                game.ChatMessages.Add(new ReversiGame.ChatMessage(DateTime.UtcNow, 0, "System", text));

                var gameNum = int.Parse(game.Id, System.Globalization.NumberStyles.HexNumber);
                var drawDetails = game.IsAiGame
                    ? $"Game outcome: draw {game.Player1PieceCount}-{game.Player2PieceCount}, reason=normal, players={(string.IsNullOrWhiteSpace(game.Player1Name) ? $"User {game.Player1UserId}" : game.Player1Name)} vs AI"
                    : $"Game outcome: draw {game.Player1PieceCount}-{game.Player2PieceCount}, reason=normal";
                _ = _gameLog.LogGameEventAsync(LogType.GameEnded, game.Player1UserId ?? 0, gameNum, drawDetails, null, game.GroupId);
            }
        }

        private void MarkFinishedWithWinner(ReversiGame game, int winner, string outcomeReason = "normal")
        {
            if (game.State == GameState.Finished)
            {
                return;
            }

            game.State = GameState.Finished;
            if (winner == 1 || winner == 2)
            {
                game.WinnerPlayer = winner;
            }

            if (!game.GameOverMessageSent)
            {
                game.GameOverMessageSent = true;
                var text = winner == 0 ? "Game over." : $"Game over. Player {winner} wins {game.Player1PieceCount}-{game.Player2PieceCount}.";
                game.ChatMessages.Add(new ReversiGame.ChatMessage(DateTime.UtcNow, 0, "System", text));
                
                // Log game end with winner and loser
                var loser = winner == 1 ? 2 : (winner == 2 ? 1 : 0);
                var winnerName = winner == 1 ? game.Player1Name : (winner == 2 ? game.Player2Name : "None");
                var loserName = loser == 1 ? game.Player1Name : (loser == 2 ? game.Player2Name : "None");
                
                if (string.IsNullOrWhiteSpace(winnerName)) winnerName = $"Player{winner}";
                if (string.IsNullOrWhiteSpace(loserName)) loserName = $"Player{loser}";
                
                if (winner == 0)
                {
                    var drawDetails = game.IsAiGame
                        ? $"Game outcome: draw/no winner, reason={outcomeReason}, players={game.Player1Name} vs AI"
                        : $"Game outcome: draw/no winner, reason={outcomeReason}, players={winnerName} vs {loserName}";
                    _ = _gameLog.LogGameEventAsync(LogType.GameEnded, game.Player1UserId ?? 0, int.Parse(game.Id, System.Globalization.NumberStyles.HexNumber), drawDetails, null, game.GroupId);
                }
                else
                {
                    var winnerUserId = winner == 1 ? game.Player1UserId : game.Player2UserId;
                    var loserUserId = winner == 1 ? game.Player2UserId : game.Player1UserId;

                    var outcomeDetails = game.IsAiGame
                        ? $"Game outcome: winner={winnerName} (Player{winner}), loser=AI, reason={outcomeReason}"
                        : $"Game outcome: winner={winnerName} (Player{winner}), loser={loserName} (Player{loser}), reason={outcomeReason}";
                    
                    if (winnerUserId.HasValue && loserUserId.HasValue && !game.IsAiGame)
                    {
                        _ = _gameLog.LogWinAsync(winnerUserId.Value, loserUserId.Value, int.Parse(game.Id, System.Globalization.NumberStyles.HexNumber), outcomeDetails, game.GroupId);
                    }
                    else
                    {
                        _ = _gameLog.LogGameEventAsync(LogType.GameEnded, winnerUserId ?? 0, int.Parse(game.Id, System.Globalization.NumberStyles.HexNumber), outcomeDetails, null, game.GroupId);
                    }
                }
            }
        }

        private static bool HasAnyLegalMove(ReversiGame game, int player)
        {
            for (var r = 0; r < 8; r++)
            {
                for (var c = 0; c < 8; c++)
                {
                    if (game.Board[r, c] != 0) continue;
                    if (GetFlipsForMove(game.Board, player, r, c).Count > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsInside(int r, int c) => r >= 0 && r < 8 && c >= 0 && c < 8;

        // Event invoker - must be inside the declaring type to invoke the event.
        private void OnGameUpdated(string gameId)
        {
            GameUpdated?.Invoke(gameId);
        }

        public int RemoveGamesExceedingRuntime(TimeSpan maxRuntime)
        {
            if (maxRuntime <= TimeSpan.Zero) return 0;

            var now = DateTime.UtcNow;
            var removed = 0;

            foreach (var kvp in _games)
            {
                var game = kvp.Value;
                var runtime = now - game.StartTimeUtc;
                if (runtime > maxRuntime)
                {
                    if (_games.TryRemove(kvp.Key, out _))
                    {
                        removed++;
                        _logger.LogInformation("RemoveGamesExceedingRuntime: removed {GameId} runtime={Runtime}", kvp.Key, runtime);
                        OnGameUpdated(kvp.Key);
                    }
                }
            }

            return removed;
        }

        public (int removed, int warningsSent) ProcessIdleTimeouts(TimeSpan maxIdle, TimeSpan killGrace, double warningFraction = 0.8)
        {
            if (maxIdle <= TimeSpan.Zero) return (0, 0);
            if (warningFraction <= 0) warningFraction = 0.8;
            if (warningFraction >= 1) warningFraction = 0.8;

            var now = DateTime.UtcNow;
            var warnAt = TimeSpan.FromTicks((long)(maxIdle.Ticks * warningFraction));
            if (killGrace <= TimeSpan.Zero) killGrace = TimeSpan.FromSeconds(1);

            var removed = 0;
            var warningsSent = 0;

            foreach (var kvp in _games)
            {
                var gameId = kvp.Key;
                var game = kvp.Value;

                var remove = false;
                var warn = false;
                var killMsg = false;
                var skipMonitoring = false;

                lock (game)
                {
                    if (game.KillAfterUtc.HasValue && now >= game.KillAfterUtc.Value)
                    {
                        remove = true;
                    }

                    if (!remove && !game.HadSecondPlayerConnected)
                    {
                        skipMonitoring = true;
                    }

                    if (skipMonitoring)
                    {
                        // Game hasn't actually started yet (no second player has connected),
                        // so don't warn/kill it for inactivity.
                        // We still allow removal if KillAfterUtc has already elapsed.
                        continue;
                    }

                    var idle = now - game.LastTimeUtc;
                    if (!remove && idle >= maxIdle)
                    {
                        if (!game.IdleKillMessageSent)
                        {
                            killMsg = true;
                            game.IdleKillMessageSent = true;
                            game.KillAfterUtc = now + killGrace;
                            game.ChatMessages.Add(new ReversiGame.ChatMessage(
                                now,
                                0,
                                "System",
                                "Game closed: Maximum move timeout - too much inactivity."));
                        }
                    }
                    else if (idle >= warnAt && !game.IdleWarningSent)
                    {
                        warn = true;
                        game.IdleWarningSent = true;

                        game.ChatMessages.Add(new ReversiGame.ChatMessage(
                            now,
                            0,
                            "System",
                            $"Warning: this game will time out after {Math.Max(1, (int)maxIdle.TotalMinutes)} minutes of inactivity."));
                    }
                }

                if (warn)
                {
                    warningsSent++;
                    OnGameUpdated(gameId);
                }

                if (killMsg)
                {
                    _logger.LogInformation("ProcessIdleTimeouts: final timeout message injected for {GameId}; will remove after {GraceSeconds}s", gameId, killGrace.TotalSeconds);
                    OnGameUpdated(gameId);
                }

                if (remove)
                {
                    if (_games.TryRemove(gameId, out var removedGame))
                    {
                        removed++;
                        _logger.LogInformation("ProcessIdleTimeouts: removed {GameId} idle>={MaxIdle}", gameId, maxIdle);
                        
                        // Log timeout with Admin ID (1) for system action
                        _ = _gameLog.LogSystemEventAsync(LogType.GameTimeout, 1, $"Game {gameId} - Maximum move timeout (idle/inactivity) - System closed game", int.Parse(gameId, System.Globalization.NumberStyles.HexNumber));
                        
                        OnGameUpdated(gameId);
                    }
                }
            }

            return (removed, warningsSent);
        }

        public int ProcessGameTimeTimeouts(TimeSpan maxGameTime)
        {
            if (maxGameTime <= TimeSpan.Zero) return 0;

            var now = DateTime.UtcNow;
            var removed = 0;
            var warnAt = TimeSpan.FromTicks((long)(maxGameTime.Ticks * 0.8)); // Warn at 80%

            foreach (var kvp in _games)
            {
                var gameId = kvp.Key;
                var game = kvp.Value;

                var shouldRemove = false;
                var shouldWarn = false;

                lock (game)
                {
                    // Only check games that have actually started (second player connected)
                    if (!game.HadSecondPlayerConnected)
                    {
                        continue;
                    }

                    // Check if game has exceeded maximum total game time
                    var gameRunTime = now - game.StartTimeUtc;
                    if (gameRunTime >= maxGameTime)
                    {
                        shouldRemove = true;
                        
                        // Add a system message before removing
                        game.ChatMessages.Add(new ReversiGame.ChatMessage(
                            now,
                            0,
                            "System",
                            $"Game closed: Maximum game time ({(int)maxGameTime.TotalMinutes} minutes) exceeded."));
                    }
                    else if (gameRunTime >= warnAt && !game.GameTimeWarningSent)
                    {
                        shouldWarn = true;
                        game.GameTimeWarningSent = true;
                        
                        game.ChatMessages.Add(new ReversiGame.ChatMessage(
                            now,
                            0,
                            "System",
                            $"Warning: this game will close after {(int)maxGameTime.TotalMinutes} minutes of total game time."));
                    }
                }

                if (shouldWarn)
                {
                    OnGameUpdated(gameId);
                }

                if (shouldRemove)
                {
                    // Notify UI BEFORE removing so it can capture the closure message
                    OnGameUpdated(gameId);
                    
                    if (_games.TryRemove(gameId, out _))
                    {
                        removed++;
                        _logger.LogInformation("ProcessGameTimeTimeouts: removed {GameId} after {MaxTime} total game time", gameId, maxGameTime);
                        
                        // Log timeout with Admin ID (1) for system action
                        _ = _gameLog.LogSystemEventAsync(LogType.GameTimeout, 1, $"Game {gameId} - Maximum game time exceeded - System closed game", int.Parse(gameId, System.Globalization.NumberStyles.HexNumber));
                        
                        // Notify again after removal so UI knows game is gone
                        OnGameUpdated(gameId);
                    }
                }
            }

            return removed;
        }

        public int ProcessGameStartWaitTimeouts(TimeSpan maxWaitTime)
        {
            if (maxWaitTime <= TimeSpan.Zero) return 0;

            var now = DateTime.UtcNow;
            var removed = 0;
            var warnAt = TimeSpan.FromTicks((long)(maxWaitTime.Ticks * 0.8)); // Warn at 80%

            foreach (var kvp in _games)
            {
                var gameId = kvp.Key;
                var game = kvp.Value;

                var shouldRemove = false;
                var shouldWarn = false;

                lock (game)
                {
                    // Only check games that haven't started yet (waiting for second player)
                    if (game.HadSecondPlayerConnected)
                    {
                        continue;
                    }

                    // Check if game has been waiting too long for players to join
                    var waitTime = now - game.CreatedUtc;
                    if (waitTime >= maxWaitTime)
                    {
                        shouldRemove = true;
                        
                        // Add a system message before removing
                        game.ChatMessages.Add(new ReversiGame.ChatMessage(
                            now,
                            0,
                            "System",
                            $"Game closed: Maximum start wait time ({(int)maxWaitTime.TotalMinutes} minutes) exceeded - no second player joined."));
                    }
                    else if (waitTime >= warnAt && !game.StartWaitWarningSent)
                    {
                        shouldWarn = true;
                        game.StartWaitWarningSent = true;
                        
                        var remaining = maxWaitTime - waitTime;
                        var remainingMins = Math.Max(0, (int)remaining.TotalMinutes);
                        var remainingSecs = Math.Max(0, remaining.Seconds);
                        
                        game.ChatMessages.Add(new ReversiGame.ChatMessage(
                            now,
                            0,
                            "System",
                            $"Warning: waiting for second player. Game will close in {remainingMins}:{remainingSecs:00}."));
                    }
                }

                if (shouldWarn)
                {
                    OnGameUpdated(gameId);
                }

                if (shouldRemove)
                {
                    // Notify UI BEFORE removing so it can capture the closure message
                    OnGameUpdated(gameId);
                    
                    if (_games.TryRemove(gameId, out _))
                    {
                        removed++;
                        _logger.LogInformation("ProcessGameStartWaitTimeouts: removed {GameId} after {MaxWait} waiting for players", gameId, maxWaitTime);
                        
                        // Log timeout with Admin ID (1) for system action
                        _ = _gameLog.LogSystemEventAsync(LogType.GameTimeout, 1, $"Game {gameId} - Maximum start wait time exceeded (no second player) - System closed game", int.Parse(gameId, System.Globalization.NumberStyles.HexNumber));
                        
                        // Notify again after removal so UI knows game is gone
                        OnGameUpdated(gameId);
                    }
                }
            }

            return removed;
        }

        public VoiceRoom? FindVoiceRoom(string roomId) => GetGame(roomId)?.Voice;

        public void OnVoiceRoomChanged(string roomId, VoiceRoomChange change)
        {
            var game = GetGame(roomId);
            if (game is null) return;

            lock (game)
            {
                game.Touch();
            }

            OnGameUpdated(roomId);

            if (change == VoiceRoomChange.Preferences)
            {
                try
                {
                    TryAnnounceVoiceInfo(roomId);
                }
                catch
                {
                }
            }
        }

        public (bool ok, string? msg) SetGameWinner(string gameId, int winnerPlayer, int userId, string userName)
        {
            if (string.IsNullOrWhiteSpace(gameId)) return (false, "No game");
            if (userId <= 0) return (false, "Invalid user");
            if (winnerPlayer != 1 && winnerPlayer != 2) return (false, "Invalid winner player");

            var game = GetGame(gameId);
            if (game is null) return (false, "Game not found");

            lock (game)
            {
                if (game.State == GameState.Finished || game.State == GameState.Abandoned)
                {
                    return (false, $"Game is {game.State}");
                }

                // Add concession message to chat
                var concessionText = $"{userName} conceded. Player {winnerPlayer} wins!";
                game.ChatMessages.Add(new ReversiGame.ChatMessage(DateTime.UtcNow, userId, "System", concessionText));

                // Mark the game as finished with the specified winner
                MarkFinishedWithWinner(game, winnerPlayer, "concession");
                game.Touch();
            }

            OnGameUpdated(gameId);
            return (true, null);
        }

        public (bool ok, string? msg) StartGame(string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId)) return (false, "No game");

            var game = GetGame(gameId);
            if (game is null) return (false, "Game not found");

            lock (game)
            {
                if (game.State == GameState.Finished || game.State == GameState.Abandoned)
                {
                    return (false, $"Game is {game.State}");
                }

                if (game.State == GameState.Connected || game.State == GameState.Playing)
                {
                    return (false, "Game already started");
                }

                // Transition from New to Connected to allow play to begin
                if (game.State == GameState.New)
                {
                    game.State = GameState.Connected;
                    game.HadSecondPlayerConnected = true;
                }

                game.Touch();
                OnGameUpdated(gameId);
                return (true, null);
            }
        }
    }

    public class ReversiGame
    {
        public string Id { get; }

        public DateTime CreatedUtc { get; } = DateTime.UtcNow;

        public DateTime StartTimeUtc { get; private set; } = DateTime.UtcNow;

        public DateTime LastTimeUtc { get; private set; } = DateTime.UtcNow;

        public bool IdleWarningSent { get; set; }

        public bool IdleKillMessageSent { get; set; }

        public bool GameTimeWarningSent { get; set; }

        public bool StartWaitWarningSent { get; set; }

        public DateTime? KillAfterUtc { get; set; }

        public ReversiService.GameState State { get; set; } = ReversiService.GameState.New;

        public bool AdminMode { get; set; } = false;

        public int? WinnerPlayer { get; set; }

        public bool GameOverMessageSent { get; set; }

        public int Player1PieceCount { get; private set; }

        public int Player2PieceCount { get; private set; }

        // Board representation:
        // 0 empty
        // 1 player1 disc (black, moves first)
        // 2 player2 disc (white)
        public int[,] Board { get; } = new int[8, 8];

        public int CurrentTurn { get; set; } = 1;

        public int CreatedByUserId { get; set; }
        public int? GroupId { get; set; }
        public int? Player1UserId { get; set; }
        public int? Player2UserId { get; set; }

        public string Player1Name { get; set; } = "";
        public string Player2Name { get; set; } = "";

        public bool Player1Connected { get; set; } = false;
        public bool Player2Connected { get; set; } = false;

        public bool IsAiGame { get; set; } = false;

        public bool HadSecondPlayerConnected { get; set; } = false;

        public bool VoiceInfoAnnounced { get; set; } = false;

        /// <summary>Voice floor, mute and per-user TTS preferences (GameVoice).</summary>
        public VoiceRoom Voice { get; } = new();

        public sealed record ChatMessage(DateTime Utc, int SenderUserId, string SenderName, string Text);

        public List<ChatMessage> ChatMessages { get; } = new();

        public sealed record BoardPos(int R, int C);

        public int? LastMoveFromR { get; set; }

        public int? LastMoveFromC { get; set; }

        public int? LastMoveToR { get; set; }

        public int? LastMoveToC { get; set; }

        public List<BoardPos> LastMoveFlippedSquares { get; } = new();

        public ReversiGame(string id)
        {
            Id = id;
            InitializeBoard();
            StartTimeUtc = DateTime.UtcNow;
            LastTimeUtc = StartTimeUtc;
        }

        public void Touch()
        {
            LastTimeUtc = DateTime.UtcNow;
            IdleWarningSent = false;
            IdleKillMessageSent = false;
            KillAfterUtc = null;
        }




        private void InitializeBoard()
        {
            // Standard Reversi/Othello opening: four discs in the centre,
            // white (player2) at d4/e5 and black (player1) at e4/d5.
            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++) Board[r, c] = 0;
            }

            Board[3, 3] = 2;
            Board[3, 4] = 1;
            Board[4, 3] = 1;
            Board[4, 4] = 2;

            // Black (player1) moves first.
            CurrentTurn = 1;
            Player1Connected = false;
            Player2Connected = false;
            Player1UserId = null;
            Player2UserId = null;

            State = ReversiService.GameState.New;
            AdminMode = false;

            Voice.ClearFloor();
            Voice.ClearPreferences();

            WinnerPlayer = null;
            GameOverMessageSent = false;

            LastMoveFromR = null;
            LastMoveFromC = null;
            LastMoveToR = null;
            LastMoveToC = null;
            LastMoveFlippedSquares.Clear();

            ChatMessages.Clear();

            RecountPieces();
        }

        public void RecountPieces()
        {
            var p1 = 0;
            var p2 = 0;

            for (var r = 0; r < 8; r++)
            {
                for (var c = 0; c < 8; c++)
                {
                    var cell = Board[r, c];
                    if (cell == 1) p1++;
                    else if (cell == 2) p2++;
                }
            }

            Player1PieceCount = p1;
            Player2PieceCount = p2;
        }
    }
}