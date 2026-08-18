package dirstore

import (
	"database/sql"
	"encoding/base64"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	_ "modernc.org/sqlite"

	"github.com/devlooped/whatsbox/internal/sqliteutil"
)

type Store struct {
	path string
	db   *sql.DB
}

type Row struct {
	Topic            string         `json:"topic"`
	Kind             string         `json:"kind"`
	Name             string         `json:"name,omitempty"`
	PN               string         `json:"pn,omitempty"`
	Icon             string         `json:"icon,omitempty"`
	Muted            bool           `json:"muted"`
	Pinned           bool           `json:"pinned"`
	Archived         bool           `json:"archived"`
	ParticipantCount int            `json:"participantCount,omitempty"`
	Participants     []Participant  `json:"participants,omitempty"`
}

type Participant struct {
	Topic string `json:"topic"`
	Name  string `json:"name,omitempty"`
	PN    string `json:"pn,omitempty"`
	Role  string `json:"role,omitempty"`
}

func Open(storeDir string) (*Store, error) {
	path := filepath.Join(storeDir, "whatsbox.db")
	uri := sqliteutil.FileURI(path, "_pragma=foreign_keys(1)&_pragma=busy_timeout(5000)")
	db, err := sql.Open("sqlite", uri)
	if err != nil {
		return nil, err
	}
	db.SetMaxOpenConns(1)
	s := &Store{path: path, db: db}
	if err := s.migrate(); err != nil {
		_ = db.Close()
		return nil, err
	}
	_ = sqliteutil.ChmodFiles(path, 0o600)
	return s, nil
}

func (s *Store) Path() string { return s.path }

func (s *Store) Close() error {
	if s == nil || s.db == nil {
		return nil
	}
	err := s.db.Close()
	s.db = nil
	return err
}

func (s *Store) migrate() error {
	_, err := s.db.Exec(`
CREATE TABLE IF NOT EXISTS chats (
  topic TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  name TEXT,
  pn TEXT,
  muted INTEGER NOT NULL DEFAULT 0,
  pinned INTEGER NOT NULL DEFAULT 0,
  archived INTEGER NOT NULL DEFAULT 0,
  participant_count INTEGER NOT NULL DEFAULT 0,
  icon_id TEXT,
  updated_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS lid_map (
  lid TEXT PRIMARY KEY,
  pn TEXT UNIQUE
);
CREATE TABLE IF NOT EXISTS participants (
  group_topic TEXT NOT NULL,
  user_topic TEXT NOT NULL,
  role TEXT,
  name TEXT,
  PRIMARY KEY (group_topic, user_topic)
);
CREATE INDEX IF NOT EXISTS chats_kind ON chats(kind);
CREATE INDEX IF NOT EXISTS chats_name ON chats(name);
`)
	return err
}

func (s *Store) Upsert(row Row) error {
	now := time.Now().Unix()
	_, err := s.db.Exec(`
INSERT INTO chats(topic, kind, name, pn, muted, pinned, archived, participant_count, icon_id, updated_at)
VALUES(?,?,?,?,?,?,?,?,?,?)
ON CONFLICT(topic) DO UPDATE SET
  kind=excluded.kind,
  name=COALESCE(NULLIF(excluded.name,''), chats.name),
  pn=COALESCE(NULLIF(excluded.pn,''), chats.pn),
  muted=excluded.muted,
  pinned=excluded.pinned,
  archived=excluded.archived,
  participant_count=CASE WHEN excluded.participant_count=0 THEN chats.participant_count ELSE excluded.participant_count END,
  icon_id=COALESCE(excluded.icon_id, chats.icon_id),
  updated_at=excluded.updated_at
`, row.Topic, row.Kind, row.Name, row.PN, boolInt(row.Muted), boolInt(row.Pinned), boolInt(row.Archived), row.ParticipantCount, nullIfEmpty(row.Icon), now)
	if err != nil {
		return err
	}
	if len(row.Participants) > 0 {
		if _, err := s.db.Exec(`DELETE FROM participants WHERE group_topic=?`, row.Topic); err != nil {
			return err
		}
		for _, p := range row.Participants {
			if _, err := s.db.Exec(`INSERT OR REPLACE INTO participants(group_topic, user_topic, role, name) VALUES(?,?,?,?)`,
				row.Topic, p.Topic, p.Role, p.Name); err != nil {
				return err
			}
		}
		if row.ParticipantCount == 0 {
			_, _ = s.db.Exec(`UPDATE chats SET participant_count=? WHERE topic=?`, len(row.Participants), row.Topic)
		}
	}
	return nil
}

func (s *Store) Remove(topic string) error {
	if _, err := s.db.Exec(`DELETE FROM participants WHERE group_topic=?`, topic); err != nil {
		return err
	}
	_, err := s.db.Exec(`DELETE FROM chats WHERE topic=?`, topic)
	return err
}

func (s *Store) Get(id string) (Row, bool, error) {
	row, ok, err := s.getExact(id)
	if err != nil || ok {
		return row, ok, err
	}
	if lid, pn, ok := s.LookupEither(id); ok {
		if r, found, err := s.getExact(lid); found || err != nil {
			if found && r.PN == "" {
				r.PN = pn
			}
			return r, found, err
		}
		if r, found, err := s.getExact(pn); found || err != nil {
			return r, found, err
		}
	}
	return Row{}, false, nil
}

func (s *Store) getExact(id string) (Row, bool, error) {
	var r Row
	var muted, pinned, archived int
	var name, pn, icon sql.NullString
	err := s.db.QueryRow(`
SELECT topic, kind, name, pn, muted, pinned, archived, participant_count, icon_id
FROM chats WHERE topic=?`, id).Scan(&r.Topic, &r.Kind, &name, &pn, &muted, &pinned, &archived, &r.ParticipantCount, &icon)
	if err == sql.ErrNoRows {
		return Row{}, false, nil
	}
	if err != nil {
		return Row{}, false, err
	}
	r.Name = name.String
	r.PN = pn.String
	r.Icon = icon.String
	r.Muted = muted != 0
	r.Pinned = pinned != 0
	r.Archived = archived != 0
	return r, true, nil
}

func (s *Store) Participants(group string) ([]Participant, error) {
	rows, err := s.db.Query(`SELECT user_topic, role, name FROM participants WHERE group_topic=? ORDER BY user_topic`, group)
	if err != nil {
		return nil, err
	}
	var out []Participant
	for rows.Next() {
		var p Participant
		var role, name sql.NullString
		if err := rows.Scan(&p.Topic, &role, &name); err != nil {
			_ = rows.Close()
			return nil, err
		}
		p.Role = role.String
		p.Name = name.String
		out = append(out, p)
	}
	err = rows.Err()
	_ = rows.Close()
	if err != nil {
		return nil, err
	}
	for i := range out {
		if lid, pn, ok := s.LookupEither(out[i].Topic); ok {
			if strings.HasSuffix(out[i].Topic, "@lid") {
				out[i].PN = pn
			} else {
				out[i].PN = out[i].Topic
				out[i].Topic = lid
			}
		}
	}
	return out, nil
}

func (s *Store) List(query, kind string, limit int, cursor string) ([]Row, string, error) {
	if limit <= 0 {
		limit = 50
	}
	if limit > 100 {
		limit = 100
	}
	offset := 0
	if cursor != "" {
		b, err := base64.StdEncoding.DecodeString(cursor)
		if err != nil {
			return nil, "", fmt.Errorf("bad cursor")
		}
		offset, _ = strconv.Atoi(string(b))
		if offset < 0 {
			offset = 0
		}
	}
	q := strings.TrimSpace(query)
	kind = strings.TrimSpace(kind)
	args := []any{}
	var where []string
	if kind != "" {
		where = append(where, "kind=?")
		args = append(args, kind)
	}
	if q != "" {
		like := "%" + q + "%"
		where = append(where, "(IFNULL(name,'') LIKE ? OR IFNULL(pn,'') LIKE ? OR topic LIKE ?)")
		args = append(args, like, like, like)
	}
	sqlStr := `SELECT topic, kind, name, pn, muted, pinned, archived, participant_count, icon_id FROM chats`
	if len(where) > 0 {
		sqlStr += " WHERE " + strings.Join(where, " AND ")
	}
	sqlStr += " ORDER BY IFNULL(name,''), topic LIMIT ? OFFSET ?"
	args = append(args, limit+1, offset)
	rows, err := s.db.Query(sqlStr, args...)
	if err != nil {
		return nil, "", err
	}
	defer rows.Close()
	var out []Row
	for rows.Next() {
		var r Row
		var muted, pinned, archived int
		var name, pn, icon sql.NullString
		if err := rows.Scan(&r.Topic, &r.Kind, &name, &pn, &muted, &pinned, &archived, &r.ParticipantCount, &icon); err != nil {
			return nil, "", err
		}
		r.Name = name.String
		r.PN = pn.String
		r.Muted = muted != 0
		r.Pinned = pinned != 0
		r.Archived = archived != 0
		// list/upsert: no icon by default
		out = append(out, r)
	}
	if err := rows.Err(); err != nil {
		return nil, "", err
	}
	var next string
	if len(out) > limit {
		out = out[:limit]
		next = base64.StdEncoding.EncodeToString([]byte(strconv.Itoa(offset + limit)))
	}
	return out, next, nil
}

func (s *Store) PutMapping(lid, pn string) error {
	lid = strings.TrimSpace(lid)
	pn = strings.TrimSpace(pn)
	if lid == "" || pn == "" {
		return nil
	}
	_, err := s.db.Exec(`INSERT INTO lid_map(lid, pn) VALUES(?,?)
ON CONFLICT(lid) DO UPDATE SET pn=excluded.pn`, lid, pn)
	return err
}

func (s *Store) LIDForPN(pn string) (string, bool) {
	var lid string
	err := s.db.QueryRow(`SELECT lid FROM lid_map WHERE pn=?`, pn).Scan(&lid)
	if err != nil {
		if phone := phoneOf(pn); phone != "" && phone != pn {
			err = s.db.QueryRow(`SELECT lid FROM lid_map WHERE pn=? OR pn=?`, topicPN(phone), pn).Scan(&lid)
		}
	}
	if err != nil {
		return "", false
	}
	return lid, true
}

func (s *Store) PNForLID(lid string) (string, bool) {
	var pn string
	err := s.db.QueryRow(`SELECT pn FROM lid_map WHERE lid=?`, lid).Scan(&pn)
	if err != nil {
		return "", false
	}
	return pn, true
}

func (s *Store) LookupEither(id string) (lid, pn string, ok bool) {
	if l, found := s.LIDForPN(id); found {
		return l, id, true
	}
	if p, found := s.PNForLID(id); found {
		return id, p, true
	}
	if phone := phoneOf(id); phone != "" {
		if l, found := s.LIDForPN(topicPN(phone)); found {
			return l, topicPN(phone), true
		}
	}
	return "", "", false
}

func (s *Store) HasMessagesTable() bool {
	var n int
	_ = s.db.QueryRow(`SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='messages'`).Scan(&n)
	return n > 0
}

func (s *Store) ContainsText(text string) bool {
	if text == "" {
		return false
	}
	var n int
	like := "%" + text + "%"
	err := s.db.QueryRow(`SELECT COUNT(*) FROM chats WHERE IFNULL(name,'') LIKE ? OR IFNULL(pn,'') LIKE ? OR IFNULL(icon_id,'') LIKE ? OR topic LIKE ?`, like, like, like, like).Scan(&n)
	return err == nil && n > 0
}

func boolInt(v bool) int {
	if v {
		return 1
	}
	return 0
}

func nullIfEmpty(s string) any {
	if s == "" {
		return nil
	}
	return s
}

func phoneOf(s string) string {
	s = strings.TrimPrefix(s, "+")
	if i := strings.IndexByte(s, '@'); i >= 0 {
		s = s[:i]
	}
	for _, r := range s {
		if r < '0' || r > '9' {
			return ""
		}
	}
	return s
}

func topicPN(phone string) string {
	return phone + "@s.whatsapp.net"
}

func TouchSessionDB(storeDir string) error {
	p := filepath.Join(storeDir, "session.db")
	f, err := os.OpenFile(p, os.O_CREATE|os.O_RDWR, 0o600)
	if err != nil {
		return err
	}
	_ = f.Close()
	return sqliteutil.ChmodFiles(p, 0o600)
}
