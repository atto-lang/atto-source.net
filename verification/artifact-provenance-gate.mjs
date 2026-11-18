import { createHash, timingSafeEqual } from 'node:crypto';

// Read-only metadata consistency check for GitHub Actions artifacts.
// IMPORTANT: a matching REST artifact record is NOT a downloaded-byte digest,
// immutable source tree proof, run-attempt attestation, or compiler/test pass.
// This function NEVER returns a certified success classification.
const SOURCE_REPO = 'ores-truffle-oreslang/oreslang-source.net';
const SOURCE_REPO_ID = 1419704375;
const SHA40 = /^[0-9a-f]{40}$/;
const fullSha = x => typeof x === 'string' && SHA40.test(x);
const DIGEST = /^sha256:[0-9a-f]{64}$/;
const validDigest = x => typeof x === 'string' && DIGEST.test(x);
const ALLOWED_TIME = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,9}))?Z$/;

const positiveInt = x => Number.isSafeInteger(x) && x > 0;
const plainObject = x => x !== null && typeof x === 'object' && !Array.isArray(x);
const safeLabel = x => typeof x === 'string' && x.length > 0 && x.length <= 250 &&
  x === x.trim() && x.normalize('NFKC') === x && !/[\p{C}\p{M}]/u.test(x);
// Date.parse silently normalizes e.g. February 30 into March 2. Treat
// timestamps as security evidence and compare the parsed UTC fields exactly.
const timestamp = x => {
  if (typeof x !== 'string') return false;
  const match = ALLOWED_TIME.exec(x);
  if (!match) return false;
  const parsed = new Date(x);
  if (!Number.isFinite(parsed.getTime())) return false;
  const [, year, month, day, hour, minute, second, fractional] = match;
  return parsed.getUTCFullYear() === Number(year) &&
    parsed.getUTCMonth() + 1 === Number(month) &&
    parsed.getUTCDate() === Number(day) &&
    parsed.getUTCHours() === Number(hour) &&
    parsed.getUTCMinutes() === Number(minute) &&
    parsed.getUTCSeconds() === Number(second) &&
    parsed.getUTCMilliseconds() === Number((fractional ?? '').padEnd(3, '0').slice(0, 3));
};

/**
 * @param {object} run independently fetched GitHub Actions run object
 * @param {object} response independently fetched COMPLETE artifact-list response
 * @param {object} expected release-controller pinned source/run/artifact identities
 * @returns {{classification:string,errors:string[],artifactIds:number[],checks:{runAttemptBound:boolean,sourceTreeBound:boolean,downloadDigestChecked:boolean}}}
 */
export function inspectArtifactMetadata(run, response, expected) {
  const errors = [];
  const deny = (ok, error) => { if (!ok) errors.push(error); };
  const checks = Object.freeze({
    runAttemptBound: false,
    sourceTreeBound: false,
    downloadDigestChecked: false,
  });
  const rejected = () => ({ classification: 'REJECTED', errors, artifactIds: [], checks });

  if (!plainObject(expected)) return { ...rejected(), errors: ['trusted expectations required'] };
  deny(expected.repo === SOURCE_REPO && expected.repositoryId === SOURCE_REPO_ID,
    'source repository numeric identity mismatch');
  deny(fullSha(expected.head), 'reviewed full source commit SHA required');
  deny(fullSha(expected.tree), 'reviewed full source tree SHA required');
  deny(positiveInt(expected.runId), 'positive run ID required');
  deny(positiveInt(expected.attempt), 'positive run attempt required');
  deny(Array.isArray(expected.requiredNames) && expected.requiredNames.length > 0 &&
    expected.requiredNames.length <= 100 &&
    expected.requiredNames.every(safeLabel) &&
    new Set(expected.requiredNames).size === expected.requiredNames.length,
    'trusted required artifact names invalid or duplicate');

  if (!plainObject(run)) {
    errors.push('run record missing');
    return rejected();
  }
  deny(run.id === expected.runId, 'workflow run ID mismatch');
  deny(run.run_attempt === expected.attempt, 'workflow attempt mismatch');
  deny(run.head_sha === expected.head, 'run source head mismatch');
  deny(run.repository?.full_name === SOURCE_REPO &&
    run.repository?.id === SOURCE_REPO_ID && run.repository?.private === true,
    'run source repository mismatch or not private');
  deny(run.status === 'completed' && run.conclusion === 'success',
    'workflow did not complete successfully');

  if (!plainObject(response) || !Array.isArray(response.artifacts)) {
    errors.push('artifact response or list missing');
    return rejected();
  }
  const artifacts = response.artifacts;
  // First-page-only results cannot be used for evidence: paginate first.
  deny(Number.isSafeInteger(response.total_count) &&
    response.total_count === artifacts.length && artifacts.length > 0 &&
    artifacts.length <= 1000, 'artifact list absent, truncated or paginated');
  if (artifacts.length > 1000) return rejected();
  const ids = new Set();
  const names = new Set();
  for (const a of artifacts) {
    if (!plainObject(a)) {
      errors.push('malformed artifact record');
      continue;
    }
    const label = safeLabel(a.name) ? a.name : '<invalid>';
    deny(positiveInt(a.id) && !ids.has(a.id), 'duplicate or invalid artifact ID');
    deny(safeLabel(a.name) && !names.has(a.name), 'duplicate or invalid artifact name');
    ids.add(a.id);
    names.add(a.name);
    deny(positiveInt(a.size_in_bytes), `artifact ${label} missing nonempty bytes`);
    deny(a.expired === false, `artifact ${label} expired or expiry unknown`);
    deny(validDigest(a.digest), `artifact ${label} missing GitHub SHA-256 metadata`);
    deny(timestamp(a.created_at), `artifact ${label} missing creation timestamp`);
    deny(timestamp(a.expires_at) && Date.parse(a.expires_at) > Date.parse(a.created_at),
      `artifact ${label} expiry timestamp invalid`);
    deny(a.workflow_run?.id === expected.runId &&
      a.workflow_run?.repository_id === SOURCE_REPO_ID &&
      a.workflow_run?.head_repository_id === SOURCE_REPO_ID &&
      a.workflow_run?.head_sha === expected.head,
      `artifact ${label} origin run/repository/SHA mismatch`);
  }
  if (Array.isArray(expected.requiredNames) && expected.requiredNames.length <= 100) {
    for (const name of expected.requiredNames) {
      if (typeof name === 'string') deny(names.has(name),
        `required artifact ${safeLabel(name) ? name : '<invalid>'} missing`);
    }
  }
  if (errors.length) return rejected();
  return {
    classification: 'METADATA_CONSISTENT_UNCERTIFIED',
    errors,
    artifactIds: [...ids].sort((a, b) => a - b),
    checks,
  };
}

/**
 * Validate actual downloaded artifact ZIP bytes against GitHub's independently
 * acquired artifact metadata. This does NOT validate ZIP member contents,
 * executable behavior, workflow attempt attribution, or the source root tree.
 * The caller must fetch GitHub JSON and download the artifact independently.
 * This routine never produces PASS or release certification.
 *
 * @param {object} run GitHub run JSON
 * @param {object} response complete run artifact-list JSON
 * @param {object} expected independently reviewed identities and required names
 * @param {string} artifactName expected artifact name
 * @param {Buffer} archiveBytes downloaded ZIP archive (not extracted entries)
 */
export function inspectDownloadedArtifactBytes(run, response, expected, artifactName, archiveBytes) {
  const metadata = inspectArtifactMetadata(run, response, expected);
  const errors = [...metadata.errors];
  const checks = Object.freeze({
    runAttemptBound: false,
    sourceTreeBound: false,
    downloadDigestChecked: false,
  });
  const rejected = () => ({classification: 'REJECTED', errors, artifactId: null, checks});
  if (metadata.classification !== 'METADATA_CONSISTENT_UNCERTIFIED') return rejected();
  if (!safeLabel(artifactName) || !expected.requiredNames.includes(artifactName)) {
    errors.push('download artifact name is not independently authorized');
    return rejected();
  }
  const record = response.artifacts.find(a => a.name === artifactName);
  if (!record || !metadata.artifactIds.includes(record.id)) {
    errors.push('download artifact is missing from validated run metadata');
    return rejected();
  }
  if (!Buffer.isBuffer(archiveBytes)) {
    errors.push('downloaded artifact must be binary bytes, not text or an untrusted object');
    return rejected();
  }
  // Conservative memory ceiling; fail closed instead of hashing an unbounded
  // archive. Large native builds need an independently audited streaming path.
  const MAX_BYTES = 256 * 1024 * 1024;
  if (archiveBytes.length === 0 || archiveBytes.length > MAX_BYTES ||
      archiveBytes.length !== record.size_in_bytes) {
    errors.push('downloaded artifact byte length does not match trusted metadata or size limit');
    return rejected();
  }
  // GitHub Actions artifact downloads are ZIP archives. This rejects obvious
  // wrong-file inputs; it does NOT attest internal ZIP entries or extraction.
  if (archiveBytes.length < 4 || archiveBytes[0] !== 0x50 || archiveBytes[1] !== 0x4b ||
      archiveBytes[2] !== 0x03 || archiveBytes[3] !== 0x04) {
    errors.push('downloaded artifact is not a ZIP local-file-header stream');
    return rejected();
  }
  const actual = createHash('sha256').update(archiveBytes).digest();
  const expectedDigest = Buffer.from(record.digest.slice('sha256:'.length), 'hex');
  if (expectedDigest.length !== 32 || !timingSafeEqual(actual, expectedDigest)) {
    errors.push('downloaded artifact bytes do not match GitHub SHA-256 digest metadata');
    return rejected();
  }
  // A hash match is meaningful but never sufficient to certify the artifact
  // was produced on the specified run attempt or contains executed binaries.
  return {
    classification: 'ARTIFACT_BYTES_MATCH_METADATA_UNCERTIFIED',
    errors,
    artifactId: record.id,
    checks: Object.freeze({
      runAttemptBound: false,
      sourceTreeBound: false,
      downloadDigestChecked: true,
    }),
  };
}
