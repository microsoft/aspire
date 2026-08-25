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
 * DER INTEGER encoding is minimal: a leading 0x00 is only legal when the next
 * byte would otherwise make the integer negative.
 * See https://www.rfc-editor.org/rfc/rfc5280#section-4.1.2.2.
 */
export function generateCertificateSerialNumber(bytes: Buffer = randomBytes(16)): string {
  if (bytes.length !== 16) {
    throw new Error(`Certificate serial numbers must use exactly 16 bytes, got ${bytes.length}.`);
  }

  const serialBytes = Buffer.from(bytes);

  // Keep the value positive without requiring DER to prepend a 0x00 byte.
  serialBytes[0] = serialBytes[0] & 0x7f;
  // node-forge strips at most one redundant leading 0x00 when DER-encoding INTEGERs.
  // Avoid a leading zero entirely so draws with multiple zero bytes cannot leave
  // illegal padding after that normalization.
  if (serialBytes[0] === 0) {
    serialBytes[0] = 1;
  }

  return serialBytes.toString('hex');
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
  cert.serialNumber = generateCertificateSerialNumber();
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
