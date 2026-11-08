-- KEYS[1] = ticker key
-- ARGV[1] = statusIdle, ARGV[2] = statusQueued, ARGV[3] = statusSkipped
-- ARGV[4] = skippedReason, ARGV[5] = executedAt (ISO), ARGV[6] = now (ISO)
-- Skips a chain child whose parent's outcome does not let it run: only while the row has not started (Idle or Queued),
-- so a child that was handed out keeps the status of its run.
-- Returns: updated JSON on success, nil when the row started or is gone
local json = redis.call('GET', KEYS[1])
if not json then return nil end
local obj = cjson.decode(json)
local status = obj['Status'] or obj['status']
if status == nil then return nil end
status = tonumber(status)
if status ~= tonumber(ARGV[1]) and status ~= tonumber(ARGV[2]) then return nil end
obj['Status'] = tonumber(ARGV[3])
obj['status'] = nil
obj['SkippedReason'] = ARGV[4]
obj['skippedReason'] = nil
obj['ExecutedAt'] = ARGV[5]
obj['executedAt'] = nil
obj['LockHolder'] = cjson.null
obj['lockHolder'] = nil
obj['LockedAt'] = cjson.null
obj['lockedAt'] = nil
obj['UpdatedAt'] = ARGV[6]
obj['updatedAt'] = nil
-- cjson writes an empty array as {}, which System.Text.Json cannot read back into a collection property;
-- turn those back into [] by key (a string value holds its quotes escaped, so it never matches).
local updated = cjson.encode(obj)
updated = (string.gsub(updated, '"Children":{}', '"Children":[]'))
updated = (string.gsub(updated, '"RetryIntervals":{}', '"RetryIntervals":[]'))
redis.call('SET', KEYS[1], updated)
return updated
