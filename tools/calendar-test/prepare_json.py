import argparse
import json
import os
from pathlib import Path
import re
import sqlite3


def main():
    parser = argparse.ArgumentParser(description="Prepare synthetic ALTONG summary JSON; never call Google APIs.")
    parser.add_argument("--database", type=Path, help="Override the read-only local SQLite database path.")
    args = parser.parse_args()
    database = args.database
    if database is None:
        local_app_data = os.environ.get("LOCALAPPDATA")
        if not local_app_data:
            raise SystemExit("LOCALAPPDATA is missing; specify --database.")
        database = Path(local_app_data) / "Altong" / "altong.db"
    if not database.is_file():
        raise SystemExit("Database not found. Start ALTONG and end a focus session.")

    connection = sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True)
    try:
        row = connection.execute(
            "SELECT session_id FROM focus_sessions "
            "WHERE ended_at IS NOT NULL AND is_completed = 1 "
            "ORDER BY julianday(ended_at) DESC LIMIT 1"
        ).fetchone()
    finally:
        connection.close()
    if row is None:
        raise SystemExit("No completed focus session. End a focus session first.")
    session_id = row[0]
    if not isinstance(session_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", session_id):
        raise SystemExit("Session ID cannot be used as an ALTONG summary filename.")

    fixture_folder = Path(__file__).resolve().parent
    payload = json.loads((fixture_folder / "sample-summary.json").read_text(encoding="utf-8-sig"))
    payload["session_id"] = session_id
    output_folder = fixture_folder.parents[1] / "verify-build" / "calendar-test-payloads"
    output_folder.mkdir(parents=True, exist_ok=True)
    output = output_folder / (session_id + ".json")
    output.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("Created:", output)
    print("In Dashboard > Notifications, click AI Summary on the KakaoTalk card and save the schedule to the dashboard calendar.")
    print("Then open Dashboard > Calendar, select the schedule date, open Google Calendar, and confirm Save in the browser.")


if __name__ == "__main__":
    main()
