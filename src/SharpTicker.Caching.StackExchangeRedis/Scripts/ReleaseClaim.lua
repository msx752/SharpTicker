-- KEYS[1] = ticker key
-- ARGV[1] = lockHolder, ARGV[2] = claimMarker (ISO, as the claim wrote it), ARGV[3] = now (ISO)
-- ARGV[4] = statusInProgress, ARGV[5] = statusIdle
-- Puts back a claim whose job never started: only while the row is InProgress, held by ARGV[1] and still carries the
-- claim's marker as its UpdatedAt. The marker is unique per node, so a row this node's fallback took is never matched.
-- Returns: updated JSON on success, nil when the row moved on or is gone
local json = redis.call('GET', KEYS[1])
if not json then return nil end
local obj = cjson.decode(json)
local status = obj['Status'] or obj['status']
if status == nil then return nil end
if tonumber(status) ~= tonumber(ARGV[4]) then return nil end
local holder = obj['LockHolder'] or obj['lockHolder']
if holder ~= ARGV[1] then return nil end
local updatedAt = obj['UpdatedAt'] or obj['updatedAt']
if updatedAt ~= ARGV[2] then return nil end
obj['LockHolder'] = cjson.null
obj['lockHolder'] = nil
obj['LockedAt'] = cjson.null
obj['lockedAt'] = nil
obj['Status'] = tonumber(ARGV[5])
obj['status'] = nil
obj['UpdatedAt'] = ARGV[3]
obj['updatedAt'] = nil
-- cjson writes an empty array as {}, which System.Text.Json cannot read back into a collection property;
-- turn those back into [] by key (a string value holds its quotes escaped, so it never matches).
local updated = cjson.encode(obj)
updated = (string.gsub(updated, '"Children":{}', '"Children":[]'))
updated = (string.gsub(updated, '"RetryIntervals":{}', '"RetryIntervals":[]'))
redis.call('SET', KEYS[1], updated)
return updated
