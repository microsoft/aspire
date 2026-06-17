import express, { Request, Response } from 'express';

const app = express();
const port = process.env.PORT || 5300;
const PLAYER_NAME = 'Node Knight';

app.use(express.json());

// Moves and their counters
const MOVES = ['rock', 'paper', 'scissors'] as const;
type Move = typeof MOVES[number];
const COUNTER: Record<Move, Move> = { rock: 'paper', paper: 'scissors', scissors: 'rock' };

// Adaptive strategy state
let roundsPlayed = 0;
let lastOwnMove: Move | null = null;
const ownMoveHistory: Move[] = [];

function chooseMove(): { move: Move; strategy: string } {
    roundsPlayed++;

    // Strategy 1: First 2 rounds - "Knight's Gambit" (always scissors, then paper)
    if (roundsPlayed <= 1) {
        lastOwnMove = 'scissors';
        ownMoveHistory.push('scissors');
        return { move: 'scissors', strategy: 'knights-gambit' };
    }
    if (roundsPlayed === 2) {
        lastOwnMove = 'paper';
        ownMoveHistory.push('paper');
        return { move: 'paper', strategy: 'knights-gambit' };
    }

    // Strategy 2: 50% chance - "Mirror Breaker" (counter our own last move to beat copycats)
    if (Math.random() < 0.5 && lastOwnMove) {
        const move = COUNTER[lastOwnMove];
        lastOwnMove = move;
        ownMoveHistory.push(move);
        return { move, strategy: 'mirror-breaker' };
    }

    // Strategy 3: "Chaos Knight" (random)
    const move = MOVES[Math.floor(Math.random() * MOVES.length)];
    lastOwnMove = move;
    ownMoveHistory.push(move);
    return { move, strategy: 'chaos-knight' };
}

app.get('/health', (_req: Request, res: Response) => {
    res.json({ status: 'healthy', player: PLAYER_NAME });
});

app.get('/api/info', (_req: Request, res: Response) => {
    res.json({
        playerName: PLAYER_NAME,
        language: 'Node.js',
        strategies: ['knights-gambit', 'mirror-breaker', 'chaos-knight'],
        personality: 'A noble knight who adapts their fighting style mid-battle',
        roundsPlayed,
    });
});

app.post('/api/move', (_req: Request, res: Response) => {
    const { move, strategy } = chooseMove();
    res.json({
        playerName: PLAYER_NAME,
        move,
        strategy,
    });
});

app.listen(port, () => {
    console.log(`${PLAYER_NAME} ready on port ${port}`);
    console.log('Strategies: knights-gambit, mirror-breaker, chaos-knight');
});
