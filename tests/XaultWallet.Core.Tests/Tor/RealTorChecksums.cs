namespace XaultWallet.Core.Tests.Tor;

/// <summary>
/// dist.torproject.org/torbrowser/15.0.24/sha256sums-signed-build.txt and its detached signature
/// (.asc) exactly as published, byte for byte. GnuPG reports a good signature by the Tor Browser
/// Developers signing subkey 022DA248432D2A0E0F54E65E316C1FACD62D07D9 over these bytes, so the
/// tests can hold the app's verifier and pinned key to real Tor Project data.
/// </summary>
internal static class RealTorChecksums
{
    public const string Version = "15.0.24";

    public const string ExpectedWindowsSha256 = "e9dc6ccc93cd6afa507193f4de284d6424233ff5102155cd2c94b259e8a22b65";

    public const string Sums = """
638ecea8b739ba30f6ff201d8b22f8511da9cee0938a2dc5584385c965af5ebc  geckodriver-linux-x86_64-15.0.24.tar.xz
d87d0732ecdca4409c0d71d6cd420399a860d0cd0981aa5866e4f2c2f1f9bc04  mar-tools-linux-i686-15.0.24.zip
6b1e9202a6280417fe92f1ce0a19b19884744df373c19460c72d23c73beabd19  mar-tools-linux-x86_64-15.0.24.zip
9374665bd1ec1510969343b55b665951505c958cf08e7a7058093aad1bba0012  mar-tools-macos-x86_64-15.0.24.zip
f0028fa1ca350a0e01162a8080cd62d4ab33f48b3c3ca1f6d2daaedfb5e4d93f  mar-tools-windows-i686-15.0.24.zip
ebe9359cfb930306d3f8b1669e175eb68397d62f52a26d5f8f1a3a986868f713  mar-tools-windows-x86_64-15.0.24.zip
c217a69a1c929a81d5217247eb10dc5306176d94b2ab7918ddd9e4c5a28e169f  src-firefox-tor-browser-140.17.0esr-15.0-1-build4.tar.xz
4ab64993293d1643b9b746f17f222478217c486226006e40711ce44e1e4d14d2  tor-browser-15.0.24-androidTest.apk.idsig
4b8d51527ab516c5e8ecf7ea25dabbcb0e4bdfb54d55eadb12ad09a36f227cda  tor-browser-android-aarch64-15.0.24.apk
8489b7dce1273ab270bf1b0f77841efa26ce315f2e3782105c9f595e86a766f0  tor-browser-android-armv7-15.0.24.apk
7f887e778a9728dd57bfcfc083f131cd9c8f7318801bf40c8eb47beb775c8dc5  tor-browser-android-x86-15.0.24.apk
2d92439e67e8e8cbc59a8821ecbef8dacd11fd64b0f305827a3023764da75b39  tor-browser-android-x86_64-15.0.24.apk
777cb5db37107fa905beccbb07e39bb7df21e94e510342f7ac1472f29b29fec2  tor-browser-debug-symbols-linux-i686-15.0.24.tar.xz
41a0cd04f7a9e181310e81c9fdae7e8461e3f266df77ea88b9b9fb608211f286  tor-browser-debug-symbols-linux-x86_64-15.0.24.tar.xz
dd58a73a896864c48df06c65fb5a5134fd2936195bec75639e60f933d04f44c2  tor-browser-debug-symbols-windows-i686-15.0.24.zip
6aa71a8e32d022e5a09a06142fd5927fcbc83edbefb719b1878b6d004d2f3d0d  tor-browser-debug-symbols-windows-x86_64-15.0.24.zip
7cf860b0f9f0bca3528b266899e8fd0de8b0586f06ad4f57ce325b165eedd197  tor-browser-linux-i686-15.0.24.mar
0eeb32d6b546713df5684ab87e2f9844c9c0af72a6c3962421a6dbd9b0b7c084  tor-browser-linux-i686-15.0.24.tar.xz
0353f8d97c9315e3d1d6ec2158ef76e1c872cc7331fb53281c30450697003b24  tor-browser-linux-x86_64-15.0.24.mar
b03eefb6ebe2acb53a1d64f82c95c0df0ced9c0f9d626586e8c6c81d9805d604  tor-browser-linux-x86_64-15.0.24.tar.xz
169e30fe19d1cba2582cb29b75df9e76d908f0a5bb84f9e3bfe73c64046910d3  tor-browser-macos-15.0.24.dmg
a67c0f80a7a18dd7d55d22954d6c2bfbbc0f046afeb0cb7bc098bc5c1a0cecf8  tor-browser-macos-15.0.24.mar
bdafb39d73ec9e007cc5eee0a3ec05fcdbb36cd357088f8f955f6492d7b1b063  tor-browser-noopt-android-aarch64-15.0.24.apk.idsig
6497fcc813aceb9d516c599bd954ffd9e5576d4363f24b072fe6b849dc0317eb  tor-browser-noopt-android-armv7-15.0.24.apk.idsig
67bfb895f50ce53a0d717f1b9ebccf06a1dc5c2934b0626d44349faee4d52dbe  tor-browser-noopt-android-x86-15.0.24.apk.idsig
736b9e97ba62a6240fa7e2d8cae86f302bc80179bbb516e71a5ded7e51dc4ff3  tor-browser-noopt-android-x86_64-15.0.24.apk.idsig
60af3e642e6e57ff82da916f7c88efbcb50c0989ec0267a19689cad2cbc4f937  tor-browser-qa-android-aarch64-15.0.24.apk
19cdefce81da4f9cc7be8bd3faea5f67556efc1dfe7b7639e57c489fd4954736  tor-browser-qa-android-aarch64-15.0.24.apk.idsig
e347d9c8173f4976a7110665038b701f2fe8f8dd2a35d5a544269763d7d377dd  tor-browser-qa-android-armv7-15.0.24.apk
789dd69f1ce9719a78d567c7a4b3667a01cca587ad331139b09ec55e9b55900c  tor-browser-qa-android-armv7-15.0.24.apk.idsig
bb3961f64a3e7b960a3f728e143a702e0b727ecf76ec19340b4337bfc1c45cf1  tor-browser-qa-android-x86-15.0.24.apk
d7f911b12dcf37927869fbb478740d89df46dcacfa120122a07eb6d2e60261d5  tor-browser-qa-android-x86-15.0.24.apk.idsig
b9d9e4d55340b24c66897394e095f9c40ae52330eceb2ac7b13de023dc8b5027  tor-browser-qa-android-x86_64-15.0.24.apk
e3be44cb1b75f461f06f443e011786067edf57df70d05ec2465573fbe2be04e2  tor-browser-qa-android-x86_64-15.0.24.apk.idsig
44a4d69040d045213e691919a5f0fc9e6da63144fd9033f2f0c7316c4bc10613  tor-browser-qa-unsign-android-aarch64-15.0.24.bspatch
a404ab61f6445e5750474e093a053ea296f4906651646c3049872c741d0ef175  tor-browser-qa-unsign-android-armv7-15.0.24.bspatch
f61a44c9da0ea4b524d4d6aafb572f848880c5646a5921f2a1b3c6bb39852e6a  tor-browser-qa-unsign-android-x86-15.0.24.bspatch
af97aab95854ee66c16d1a91dc1020ad7c4a5ecf0196a4849d40f7ade19efead  tor-browser-qa-unsign-android-x86_64-15.0.24.bspatch
34c73ac0f4f5320d6e1d6d75b7991fd6769f5037d707a07ab8df07fe10cf3604  tor-browser-windows-i686-15.0.24.mar
d30acc71e3413a58b6f405421e72cf222e6633576ffa9e95750f3f56821668b8  tor-browser-windows-i686-portable-15.0.24.exe
75b603ec80e4c1d2c3b286469534de40a2f37792685b180260ab5d4725b9f52c  tor-browser-windows-x86_64-15.0.24.mar
a549824c31dd9ee8143ecc6bc97b8f2d5fd5cba8b13f528f1444a0b769758c6c  tor-browser-windows-x86_64-portable-15.0.24.exe
2a7a02484149b05f64b582a4a1e8eb8aabb9f6cd32daca4e03c58e09839bb7a8  tor-expert-bundle-android-aarch64-15.0.24.tar.gz
9416f72b9cd31eda0f7f5a1fa3e2eec789b88b8160e20f5d690bc4ba8c29811e  tor-expert-bundle-android-armv7-15.0.24.tar.gz
32532b4250d5823f07ac2d88197908c1d11ffa015354144d295ee64302107917  tor-expert-bundle-android-x86-15.0.24.tar.gz
500d51006f1938dfd5ecd0653b0d2a156feff8a4dc5e2b61b81793d47854fc90  tor-expert-bundle-android-x86_64-15.0.24.tar.gz
7537fea3478d05b8af25d7f8199c031b281f7015c32bb4177bef71f8e5100d9b  tor-expert-bundle-linux-i686-15.0.24.tar.gz
8e012ec6815d7899cb64011582e2dade88e74119c6661068a2a3252de0ccd7f2  tor-expert-bundle-linux-x86_64-15.0.24.tar.gz
d47afd04b6c751129978390ad003d74ac8b88adfbb939350f0f89999e6570644  tor-expert-bundle-macos-aarch64-15.0.24.tar.gz
8acb0b590f6be34084dcb6d84009ac0c61cc7c5261b7a19d2ab94845aa9bd5b6  tor-expert-bundle-macos-x86_64-15.0.24.tar.gz
7c2755b09876ebc6c2e2d2d1d3279b2be3e9beec35ff0ca2e7df4c1abcad1ae4  tor-expert-bundle-windows-i686-15.0.24.tar.gz
e9dc6ccc93cd6afa507193f4de284d6424233ff5102155cd2c94b259e8a22b65  tor-expert-bundle-windows-x86_64-15.0.24.tar.gz
""" + "\n";

    public const string Signature = """
-----BEGIN PGP SIGNATURE-----

iQIzBAABCgAdFiEEAi2iSEMtKg4PVOZeMWwfrNYtB9kFAmq7t9cACgkQMWwfrNYt
B9lEug//dAADL6K81gCIn03b8/Cq/5dNCn1K6CsgikS3kqLca1WUtfDdYVHn3Pa3
mk3jL7YkPNSddvgrsX05CU+7Vl2mNEUX/ElJEUau6+0IhdpWKC79IlSj6JQKvvGJ
u49MjsWEUVgoCBv33VIfoHeyc3DPmoCYZ1et8nZcvuXLRMZKN0GMNRW3nqjqeBJb
9HY+PTYBbLewJNYbHWYN6Wwit6HCO6w33UhIcGiPopNv0JPsUMk2HaueFx7sbrBL
vZ0QRPkSkdj6dUHvz5xDkLo5ID6hCBx1s7YBWQ7usm69zfXPw7GhdgBaga2Y6DEs
2SLGr6FGlrpeOWXvpBmupgQRVi9q+MJOMk4n7tUAER5ypnPeWQsm+9hlv6wRflEU
cZyWUk7ITwCHwkWSm3A5og8qtOUBS7pnawDgwnf2ccC2CQaFHH1oew9evuM1XWni
30Bnof/JmnKs9IKkwS4V+o0o/Yd/cRRBkdwRSg6LXs2ZOHtv762jWX4sA7gu8fsC
bY3HCjZ2LiKYyevSNM/w8oTIEwl7p6OzTl1Qy3oCFStw7W2xk8j7cK+MjGa4Qy2r
gET7DHFhNhjBh8GJuC5c2fVPvOGydV2GZ5HEFQqY5RAvg5+flynTt0ZcgemSsLgx
LgZLB/2a5B2ACprxKt+w2bhiBb+Gd8hZknD3LxBqlrg4mzt+tWc=
=2Tmp
-----END PGP SIGNATURE-----
""" + "\n";
}
