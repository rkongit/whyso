-- Capture HEAD commit SHA at session end (start SHA remains in git_head).
ALTER TABLE sessions ADD COLUMN git_head_after TEXT;
