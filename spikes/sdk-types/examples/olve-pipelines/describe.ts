// Runs describe mode on the example and prints the manifest (exercises the recording proxies).
import p from "./pipeline";
console.log(JSON.stringify(p.describe(), null, 2));
