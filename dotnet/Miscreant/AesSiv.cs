using System;
using System.Security.Cryptography;

namespace Miscreant
{
	/// <summary>
	/// AES-SIV authenticated encryption mode, defined in
	/// <see href="https://tools.ietf.org/html/rfc5297">RFC 5297</see>.
	/// </summary>
	public sealed class AesSiv : IDisposable
	{
		private const int BlockSize = Constants.BlockSize;
		private const int AesSiv256KeySize = 32;
		private const int AesSiv512KeySize = 64;
		private const int MaxAssociatedDataItems = 126;
		private static readonly byte[] Zero = new byte[BlockSize];

		private readonly KeyedHashAlgorithm mac;
		private readonly byte[] K1;
		private readonly byte[] K2;
		private bool disposed;

		/// <summary>
		/// Initializes a new instance of the <see cref="AesSiv"/> class with the specified key.
		/// </summary>
		/// <param name="key">The secret key for <see cref="AesSiv"> encryption.</param>
		public AesSiv(byte[] key)
		{
			if (key == null)
			{
				throw new ArgumentNullException(nameof(key));
			}

			if (key.Length != AesSiv256KeySize && key.Length != AesSiv512KeySize)
			{
				throw new CryptographicException("Specified key is not a valid size for this algorithm.");
			}

			int halfKeySize = key.Length / 2;

			K1 = new byte[halfKeySize];
			K2 = new byte[halfKeySize];

			Array.Copy(key, 0, K1, 0, halfKeySize);
			Array.Copy(key, halfKeySize, K2, 0, halfKeySize);

			mac = new AesCmac(K1);
		}

		/// <summary>
		/// Seal encrypts and authenticates plaintext, authenticates the given
		/// associated data items, and returns the result. For nonce-based
		/// encryption, the nonce should be the last associated data item.
		/// </summary>
		/// <param name="plaintext">The plaintext to encrypt.</param>
		/// <param name="data">Associated data items to authenticate.</param>
		/// <returns>Concatenation of the authentication tag and the encrypted data.</returns>
		public byte[] Seal(byte[] plaintext, params byte[][] data)
		{
			if (plaintext == null)
			{
				throw new ArgumentNullException(nameof(plaintext));
			}

			if (data == null)
			{
				throw new ArgumentNullException(nameof(data));
			}

			if (data.Length > MaxAssociatedDataItems)
			{
				throw new CryptographicException($"Maximum number of associated data items is {MaxAssociatedDataItems}");
			}

			byte[] iv = S2V(data, plaintext);
			byte[] output = new byte[iv.Length + plaintext.Length];

			Array.Copy(iv, output, iv.Length);
			ZeroIvBits(iv);

			using (var ctr = new AesCtr(K2, iv))
			{
				ctr.Encrypt(plaintext, 0, plaintext.Length, output, iv.Length);
				return output;
			}
		}
		/// <summary>
		/// Open decrypts ciphertext, authenticates the decrypted plaintext
		/// and the given associated data items and, if successful, returns
		/// the result. For nonce-based encryption, the nonce should be the
		/// last associated data item.
		/// </summary>
		/// <param name="ciphertext">The ciphertext to decrypt.</param>
		/// <param name="data">Associated data items to authenticate.</param>
		/// <returns>The decrypted plaintext.</returns>
		public byte[] Open(byte[] ciphertext, params byte[][] data)
		{
			if (ciphertext == null)
			{
				throw new ArgumentNullException(nameof(ciphertext));
			}

			if (data == null)
			{
				throw new ArgumentNullException(nameof(data));
			}

			if (ciphertext.Length < BlockSize)
			{
				throw new CryptographicException("Malformed or corrupt ciphertext.");
			}

			if (data.Length > MaxAssociatedDataItems)
			{
				throw new CryptographicException($"Maximum number of associated data items is {MaxAssociatedDataItems}");
			}

			byte[] iv = new byte[BlockSize];
			byte[] output = new byte[ciphertext.Length - iv.Length];

			Array.Copy(ciphertext, 0, iv, 0, BlockSize);
			ZeroIvBits(iv);

			using (var ctr = new AesCtr(K2, iv))
			{
				ctr.Encrypt(ciphertext, BlockSize, output.Length, output, 0);
			}

			byte[] v = S2V(data, output);

			if (!ConstantTimeEquals(ciphertext, v, BlockSize))
			{
				throw new CryptographicException("Malformed or corrupt ciphertext.");
			}

			return output;
		}

		/// <summary>
		/// S2V operation, defined in the section 2.4 of
		/// <see href="https://tools.ietf.org/html/rfc5297#section-2.4">RFC 5297</see>.
		/// </summary>
		private byte[] S2V(byte[][] headers, byte[] message)
		{
			if (headers == null)
			{
				throw new ArgumentNullException(nameof(headers));
			}

			if (message == null)
			{
				throw new ArgumentNullException(nameof(message));
			}

			// The standalone S2V returns CMAC(1) if the number of
			// passed vectors is zero, however in SIV contruction
			// this case is never triggered, since we always pass
			// plaintext as the last vector (even if it's zero-length),
			// so we omit this case.

			mac.TransformFinalBlock(Zero, 0, BlockSize);
			byte[] v = mac.Hash;

			foreach (var header in headers)
			{
				if (header == null)
				{
					throw new ArgumentNullException(nameof(header));
				}

				mac.TransformFinalBlock(header, 0, header.Length);
				Utils.Multiply(v);
				Utils.Xor(mac.Hash, v, BlockSize);
			}

			if (message.Length > BlockSize)
			{
				int n = message.Length - BlockSize;

				mac.TransformBlock(message, 0, n, message, 0);
				Utils.Xor(message, n, v, 0, BlockSize);
				mac.TransformFinalBlock(v, 0, BlockSize);

				return mac.Hash;
			}

			byte[] padded = new byte[BlockSize];

			Array.Copy(message, padded, message.Length);
			Utils.Multiply(v);
			Utils.Pad(padded, message.Length);
			Utils.Xor(padded, v, BlockSize);
			mac.TransformFinalBlock(v, 0, BlockSize);

			return mac.Hash;
		}

		private void ZeroIvBits(byte[] iv)
		{
			iv[iv.Length - 8] &= 0x7f;
			iv[iv.Length - 4] &= 0x7f;
		}

		private bool ConstantTimeEquals(byte[] x, byte[] y, int count)
		{
			byte result = 0;

			for (int i = 0; i < count; ++i)
			{
				result |= (byte)(x[i] ^ y[i]);
			}

			return result == 0;
		}

		public void Dispose()
		{
			if (!disposed)
			{
				mac.Dispose();

				Array.Clear(K1, 0, BlockSize);
				Array.Clear(K2, 0, BlockSize);

				disposed = true;
			}
		}
	}
}
