import { randomBytes, X509Certificate } from 'crypto';
import forge from 'node-forge';

interface SelfSignedCert {
    key: string;
    cert: string;
    certBase64: string;
}

/**
 * Generates a valid X.509 serial number as a hexadecimal string.
 * Per RFC 5280, serial numbers must be positive integers up to 20 octets.
 * DER encoding requires minimal representation (no unnecessary leading zeros)
 * and a leading 0x00 byte if the high bit of the first byte is set (to ensure positive).
 */
function generateSerialNumber(): string {
  // Generate 16 random bytes (128 bits) - plenty of entropy while leaving room for padding
  const bytes = randomBytes(16);

  // Ensure the first byte doesn't have its high bit set to avoid needing a 0x00 prefix.
  // This guarantees the number is positive without extra padding.
  // We mask off the high bit (AND with 0x7F) to ensure it's always < 128.
  bytes[0] = bytes[0] & 0x7f;

  // Ensure the serial number is non-zero by setting the least significant bit if all bytes are zero
  // (extremely unlikely with 16 random bytes, but handles the edge case)
  if (bytes.every(b => b === 0)) {
    bytes[15] = 1;
  }

  return bytes.toString('hex');
}

export async function createSelfSignedCertAsync(commonName: string = 'localhost'): Promise<SelfSignedCert> {
  const pki = forge.pki;
  const keys = await new Promise<forge.pki.rsa.KeyPair>((resolve, reject) => {
    // 4096 bits provides enough entropy. Follows modern industry practice
    pki.rsa.generateKeyPair({ bits: 4096, workers: -1 }, (err, keypair) => {
      if (err) {
        reject(err);
      } else {
        resolve(keypair);
      }
    });
  });

  const cert = pki.createCertificate();
  cert.publicKey = keys.publicKey;
  cert.serialNumber = generateSerialNumber();
  cert.validity.notBefore = new Date();
  cert.validity.notAfter = new Date();
  cert.validity.notAfter.setFullYear(cert.validity.notBefore.getFullYear() + 1);

  const attrs = [{ name: 'commonName', value: commonName }];
  cert.setSubject(attrs);
  cert.setIssuer(attrs);

  // Add SAN extension for localhost
  cert.setExtensions([
    {
      name: 'subjectAltName',
      altNames: [
        { type: 2, value: 'localhost' }, // DNS
      ]
    }
  ]);

  cert.sign(keys.privateKey);

  const certPem = pki.certificateToPem(cert);
  const x509Cert = new X509Certificate(certPem);

  return {
    key: pki.privateKeyToPem(keys.privateKey),
    cert: certPem,
    certBase64: x509Cert.raw.toString('base64')
  };
}

export function generateToken(): string {
    // 32 bytes is used to provide sufficient entropy for security (2^256 possibilities)
    const key = randomBytes(32);
    return key.toString('base64');
}
