"""
Rock Paper Scissors - Python Serpent Player
A cunning player that uses pattern analysis and weighted randomness.
"""

import json
import os
import random
from http.server import HTTPServer, BaseHTTPRequestHandler

PORT = int(os.environ.get("PORT", "5200"))
PLAYER_NAME = "Python Serpent"

# Track opponent's recent moves to find patterns
opponent_history: list[str] = []
MOVES = ["rock", "paper", "scissors"]
COUNTER = {"rock": "paper", "paper": "scissors", "scissors": "rock"}


def choose_move() -> tuple[str, str]:
    """Pick a move using pattern analysis with some randomness."""
    # Not enough history - use weighted random favoring rock (classic opener)
    if len(opponent_history) < 3:
        weights = [0.4, 0.35, 0.25]  # Slightly favor rock and paper
        move = random.choices(MOVES, weights=weights, k=1)[0]
        return move, "weighted-random"

    # Analyze opponent's last 5 moves for patterns
    recent = opponent_history[-5:]
    freq = {m: recent.count(m) for m in MOVES}
    most_common = max(freq, key=lambda m: freq[m])

    # 60% chance: counter their most frequent recent move
    # 40% chance: random (to stay unpredictable)
    if random.random() < 0.6:
        move = COUNTER[most_common]
        return move, "pattern-counter"
    else:
        move = random.choice(MOVES)
        return move, "chaos-serpent"


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/health":
            self._respond(200, {"status": "healthy", "player": PLAYER_NAME})
        elif self.path == "/api/info":
            self._respond(200, {
                "playerName": PLAYER_NAME,
                "language": "Python",
                "strategies": ["weighted-random", "pattern-counter", "chaos-serpent"],
                "personality": "A cunning serpent that studies its prey before striking",
                "gamesAnalyzed": len(opponent_history),
            })
        else:
            self._respond(404, {"error": "not found"})

    def do_POST(self):
        if self.path == "/api/move":
            move, strategy = choose_move()
            self._respond(200, {
                "playerName": PLAYER_NAME,
                "move": move,
                "strategy": strategy,
            })
        elif self.path == "/api/opponent-move":
            length = int(self.headers.get("Content-Length", 0))
            body = {}
            if length:
                body = json.loads(self.rfile.read(length))
            if "move" in body:
                opponent_history.append(body["move"])
            self._respond(200, {"recorded": True})
        else:
            self._respond(404, {"error": "not found"})

    def _respond(self, status: int, data: dict):
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(json.dumps(data).encode())

    def log_message(self, format, *args):
        print(f"[{PLAYER_NAME}] {args[0]}")


if __name__ == "__main__":
    server = HTTPServer(("0.0.0.0", PORT), Handler)
    print(f"{PLAYER_NAME} ready on port {PORT}")
    print(f"Strategies: weighted-random, pattern-counter, chaos-serpent")
    server.serve_forever()
