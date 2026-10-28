// Tiny Garage helper for validate.sh. Credentials come from env (never printed).
//   S3_ACCESS_KEY, S3_SECRET_KEY   Garage key
//   S3_UPLOAD_ENDPOINT             endpoint reachable from here (e.g. kubectl port-forward)
//   S3_SIGN_ENDPOINT               endpoint the pod uses — the Host baked into the SigV4 signature
//   S3_BUCKET, S3_REGION (default us-east-1)
// Commands:
//   put <file> <key>               upload
//   presign <key> <outfile> [secs] write a pre-signed GET URL to <outfile> (URL holds the key id)
//   list <prefix>                  print keys
//   rm-prefix <prefix>             delete every key under prefix
import { S3Client } from "bun";

const env = (k: string, d?: string) => {
  const v = process.env[k] ?? d;
  if (!v) throw new Error(`${k} not set`);
  return v;
};
const base = {
  accessKeyId: env("S3_ACCESS_KEY"),
  secretAccessKey: env("S3_SECRET_KEY"),
  bucket: env("S3_BUCKET"),
  region: env("S3_REGION", "us-east-1"),
};
const up = new S3Client({ ...base, endpoint: env("S3_UPLOAD_ENDPOINT") });
const sign = new S3Client({ ...base, endpoint: env("S3_SIGN_ENDPOINT") });

const [cmd, a, b, c] = process.argv.slice(2);
switch (cmd) {
  case "put": {
    const n = await up.write(b, Bun.file(a));
    console.log(`put ${b} (${n} bytes)`);
    break;
  }
  case "presign": {
    await Bun.write(b, sign.presign(a, { method: "GET", expiresIn: Number(c ?? 3600) }));
    console.log(`presigned ${a} -> ${b}`);
    break;
  }
  case "list": {
    const r = await up.list({ prefix: a });
    for (const o of r.contents ?? []) console.log(`${o.key} ${o.size}`);
    console.log(`(${r.contents?.length ?? 0} objects under ${a})`);
    break;
  }
  case "rm-prefix": {
    if (!a?.startsWith("spike/")) throw new Error("refusing to delete outside spike/");
    const r = await up.list({ prefix: a });
    for (const o of r.contents ?? []) {
      await up.delete(o.key);
      console.log(`deleted ${o.key}`);
    }
    break;
  }
  default:
    console.error("usage: s3.ts put|presign|list|rm-prefix ...");
    process.exit(64);
}
