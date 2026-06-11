import { useState, useEffect, useCallback } from 'react';

interface RoundResult {
    id: number;
    player1Name: string;
    player1Move: string;
    player2Name: string;
    player2Move: string;
    winner: string;
    playedAt: string;
}

interface LeaderboardEntry {
    playerName: string;
    wins: number;
    losses: number;
    draws: number;
    totalRounds: number;
}

interface PlayerInfo {
    playerName: string;
    language: string;
    strategies: string[];
    personality: string;
}

const MOVE_EMOJI: Record<string, string> = {
    rock: '🪨',
    paper: '📄',
    scissors: '✂️',
};

export default function App() {
    const [lastRounds, setLastRounds] = useState<RoundResult[]>([]);
    const [rounds, setRounds] = useState<RoundResult[]>([]);
    const [leaderboard, setLeaderboard] = useState<LeaderboardEntry[]>([]);
    const [fighting, setFighting] = useState(false);
    const [players, setPlayers] = useState<Record<string, PlayerInfo>>({});
    const [openTooltip, setOpenTooltip] = useState<string | null>(null);

    const fetchData = useCallback(async () => {
        const [roundsRes, lbRes] = await Promise.all([
            fetch('/api/rounds?limit=10'),
            fetch('/api/leaderboard'),
        ]);
        if (roundsRes.ok) setRounds(await roundsRes.json());
        if (lbRes.ok) setLeaderboard(await lbRes.json());
    }, []);

    useEffect(() => {
        fetch('/api/players').then(async (res) => {
            if (res.ok) {
                const infos: PlayerInfo[] = await res.json();
                const map: Record<string, PlayerInfo> = {};
                for (const p of infos) {
                    if (p?.playerName) map[p.playerName] = p;
                }
                setPlayers(map);
            }
        });
    }, []);

    useEffect(() => {
        fetchData();
    }, [fetchData]);

    useEffect(() => {
        if (!openTooltip) return;
        const close = () => setOpenTooltip(null);
        document.addEventListener('click', close);
        return () => document.removeEventListener('click', close);
    }, [openTooltip]);

    const fight = async () => {
        setFighting(true);
        try {
            const res = await fetch('/api/rounds/play', { method: 'POST' });
            if (res.ok) {
                const played: RoundResult[] = await res.json();
                setLastRounds(played);
                await fetchData();
            }
        } finally {
            setFighting(false);
        }
    };

    const clearHistory = async () => {
        if (!confirm('Are you sure you want to clear all battle history?')) return;
        const res = await fetch('/api/rounds', { method: 'DELETE' });
        if (res.ok) {
            setLastRounds([]);
            await fetchData();
        }
    };

    const winnerClass = (round: RoundResult) =>
        round.winner === 'draw' ? 'draw' : 'win';

    const winnerLabel = (round: RoundResult) => {
        if (round.winner === 'draw') return '🤝 Draw!';
        return `🏆 ${round.winner} wins!`;
    };

    const PlayerName = ({ name, id }: { name: string; id: string }) => {
        const info = players[name];
        if (!info) return <span>{name}</span>;
        const isOpen = openTooltip === id;
        return (
            <span className="player-name-wrapper">
                {name}
                <button
                    className="info-btn"
                    onClick={(e) => { e.stopPropagation(); setOpenTooltip(isOpen ? null : id); }}
                    aria-label={`Info about ${name}`}
                >
                    ℹ️
                </button>
                {isOpen && (
                    <div className="player-tooltip">
                        <div className="tooltip-header">{info.playerName}</div>
                        <div className="tooltip-lang">{info.language}</div>
                        <div className="tooltip-personality">"{info.personality}"</div>
                        <div className="tooltip-strategies">
                            {info.strategies.map((s) => (
                                <span key={s} className="strategy-tag">{s}</span>
                            ))}
                        </div>
                    </div>
                )}
            </span>
        );
    };

    return (
        <div className="arena">
            <h1>⚔️ RPS Arena ⚔️</h1>
            <p className="subtitle">
                Python Serpent vs Node Knight vs C# Paladin — powered by Aspire
            </p>

            <div className="action-buttons">
                <button className="fight-btn" onClick={fight} disabled={fighting}>
                    {fighting ? '⚔️ Fighting...' : '🎲 FIGHT!'}
                </button>
                <button className="clear-btn" onClick={clearHistory}>
                    🗑️ Clear History
                </button>
            </div>

            {lastRounds.length > 0 && (
                <div className="last-round">
                    <h2>Latest Round</h2>
                    <div className="matches-row">
                    {lastRounds.map((match) => (
                        <div key={match.id} className="match-result">
                            <div className="versus">
                                <div className="player-card">
                                    <div className="name"><PlayerName name={match.player1Name} id={`match-${match.id}-p1`} /></div>
                                    <div className="move">{MOVE_EMOJI[match.player1Move] || '❓'}</div>
                                    <div className="move-label">{match.player1Move}</div>
                                </div>
                                <div className="vs-text">VS</div>
                                <div className="player-card">
                                    <div className="name"><PlayerName name={match.player2Name} id={`match-${match.id}-p2`} /></div>
                                    <div className="move">{MOVE_EMOJI[match.player2Move] || '❓'}</div>
                                    <div className="move-label">{match.player2Move}</div>
                                </div>
                            </div>
                            <div className={`result-text ${winnerClass(match)}`}>
                                {winnerLabel(match)}
                            </div>
                        </div>
                    ))}
                    </div>
                </div>
            )}

            <div className="sections">
                <div className="section">
                    <h2>🏆 Leaderboard</h2>
                    {leaderboard.length === 0 ? (
                        <div className="empty-state">No battles yet — hit FIGHT!</div>
                    ) : (
                        <table>
                            <thead>
                                <tr>
                                    <th>Player</th>
                                    <th>W</th>
                                    <th>L</th>
                                    <th>D</th>
                                    <th>Total</th>
                                </tr>
                            </thead>
                            <tbody>
                                {leaderboard.map((entry) => (
                                    <tr key={entry.playerName}>
                                        <td><PlayerName name={entry.playerName} id={`lb-${entry.playerName}`} /></td>
                                        <td style={{ color: 'var(--win)' }}>{entry.wins}</td>
                                        <td style={{ color: 'var(--loss)' }}>{entry.losses}</td>
                                        <td style={{ color: 'var(--draw)' }}>{entry.draws}</td>
                                        <td>{entry.totalRounds}</td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    )}
                </div>

                <div className="section">
                    <h2>📜 Battle History</h2>
                    {rounds.length === 0 ? (
                        <div className="empty-state">No history yet</div>
                    ) : (
                        rounds.map((r) => (
                            <div className="history-item" key={r.id}>
                                <span>
                                    {r.player1Name} {MOVE_EMOJI[r.player1Move]} vs {MOVE_EMOJI[r.player2Move]} {r.player2Name}
                                </span>
                                <span className={`winner-tag ${r.winner === 'draw' ? 'draw' : r.winner === r.player1Name ? 'p1' : 'p2'}`}>
                                    {r.winner === 'draw' ? 'Draw' : r.winner}
                                </span>
                            </div>
                        ))
                    )}
                </div>
            </div>
        </div>
    );
}
