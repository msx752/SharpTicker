-- KEYS[1] = ticker key
-- ARGV[1] = lockHolder, ARGV[2] = now (ISO), ARGV[3] = statusQueued, ARGV[4] = statusInProgress
-- Fenced claim: moves the row to InProgress only while it is still Queued and held by ARGV[1].
-- Returns: updated JSON on success, nil on failure
local json = redis.call('GET', KEYS[1])
if not json then return nil end
local obj = cjson.decode(json)
local status = obj['Status'] or obj['status']
if status == nil then return nil end
if tonumber(status) ~= tonumber(ARGV[3]) then return nil end
local holder = obj['LockHolder'] or obj['lockHolder']
if holder ~= ARGV[1] then return nil end
obj['Status'] = tonumber(ARGV[4])
obj['status'] = nil
obj['UpdatedAt'] = ARGV[2]
obj['updatedAt'] = nil
-- cjson writes an empty array as {}, which System.Text.Json cannot read back into a collection property;
-- turn those back into [] by key (a string value holds its quotes escaped, so it never matches).
local updated = cjson.encode(obj)
updated = (string.gsub(updated, '"Children":{}', '"Children":[]'))
updated = (string.gsub(updated, '"RetryIntervals":{}', '"RetryIntervals":[]'))
redis.call('SET', KEYS[1], updated)
return updated
