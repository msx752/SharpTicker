-- KEYS[1] = row key
-- ARGV[1] = the JSON the caller read, ARGV[2] = the JSON to write
-- Writes the row only when it still holds exactly what the caller read, so a read-modify-write in C# never overwrites a
-- write made in between. The rules (fence, status) are checked in C# on the row that was read; this compares text only.
-- Returns: 1 when written, 0 when the row changed since it was read, -1 when the key is gone
local current = redis.call('GET', KEYS[1])
if not current then return -1 end
if current ~= ARGV[1] then return 0 end
redis.call('SET', KEYS[1], ARGV[2])
return 1
