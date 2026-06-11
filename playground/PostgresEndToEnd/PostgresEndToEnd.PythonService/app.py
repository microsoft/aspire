import os
import uuid
import psycopg
from fastapi import FastAPI
from entra_connection import get_entra_conninfo

app = FastAPI()

def get_connection():
    uri = os.environ['DB1_URI']
    user = os.environ.get("DB1_USERNAME")
    password = os.environ.get("DB1_PASSWORD")
    if not password:
        # use entra auth
        entra_conninfo = get_entra_conninfo(None)
        password = entra_conninfo["password"]
        if not user:
            # If user isn't already set, use the username from the token
            user = entra_conninfo["user"]

    return psycopg.connect(uri, user=user, password=password)

@app.get('/')
def index():
    conn = get_connection()
    with conn.cursor() as cur:
        cur.execute("CREATE TABLE IF NOT EXISTS entries (id UUID PRIMARY KEY);")
        entry_id = str(uuid.uuid4())
        cur.execute("INSERT INTO entries (id) VALUES (%s);", (entry_id,))
        conn.commit()
        cur.execute("SELECT id FROM entries;")
        entries = [row[0] for row in cur.fetchall()]
    conn.close()
    return {'totalEntries': len(entries), 'entries': entries}
