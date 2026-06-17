import { useState, useEffect } from 'react';

interface ApiResponse {
  message: string;
  database?: string;
  serverTime?: string;
  error?: string;
}

function App() {
  const [data, setData] = useState<ApiResponse | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    fetch('/api/')
      .then(res => {
        if (!res.ok) {
          throw new Error(`HTTP ${res.status}: ${res.statusText}`);
        }
        return res.json();
      })
      .then((data: ApiResponse) => {
        setData(data);
        setLoading(false);
      })
      .catch(err => {
        console.error('API Error:', err);
        setData({ message: 'Error connecting to API', error: err.message });
        setLoading(false);
      });
  }, []);

  return (
    <div style={{
      fontFamily: 'system-ui, sans-serif',
      maxWidth: '600px',
      margin: '0 auto',
      padding: '2rem'
    }}>
      <h1>Aspire + Vite + React</h1>
      <p>Frontend connected to Express API + PostgreSQL</p>

      {loading ? (
        <p>Loading...</p>
      ) : data ? (
        <div style={{
          background: '#f0f0f0',
          padding: '1rem',
          borderRadius: '8px',
          marginTop: '1rem'
        }}>
          <p><strong>API Response:</strong></p>
          <pre style={{
            background: '#fff',
            padding: '1rem',
            borderRadius: '4px',
            overflow: 'auto'
          }}>
            {JSON.stringify(data, null, 2)}
          </pre>
        </div>
      ) : null}
    </div>
  );
}

export default App;
