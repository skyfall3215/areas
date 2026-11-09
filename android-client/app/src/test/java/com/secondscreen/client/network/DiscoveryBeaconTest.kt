package com.secondscreen.client.network

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class DiscoveryBeaconTest {

    @Test
    fun parse_jsonBeacon_success() {
        val json = """
            {
               "magic": "SECOND_SCREEN_DISCOVERY",
               "version": 1,
               "hostname": "Gaming-PC",
               "port": 27183,
               "addresses": ["192.168.1.150"]
            }
        """.trimIndent()

        val host = DiscoveryBeaconParser.parse(json, "192.168.1.150")
        assertNotNull(host)
        assertEquals("Gaming-PC", host?.hostname)
        assertEquals(27183, host?.port)
        assertEquals("192.168.1.150", host?.ipAddress)
    }

    @Test
    fun parse_textBeacon_success() {
        val text = "SECOND_SCREEN_HOST:27183:Laptop-Host"
        val host = DiscoveryBeaconParser.parse(text, "192.168.1.200")
        assertNotNull(host)
        assertEquals("Laptop-Host", host?.hostname)
        assertEquals(27183, host?.port)
        assertEquals("192.168.1.200", host?.ipAddress)
    }

    @Test
    fun parse_invalidData_returnsNull() {
        assertNull(DiscoveryBeaconParser.parse("", "192.168.1.1"))
        assertNull(DiscoveryBeaconParser.parse(ByteArray(0), "192.168.1.1"))
        assertNull(DiscoveryBeaconParser.parse("RANDOM_UNRECOGNIZED_PACKET", "192.168.1.1"))
        assertNull(DiscoveryBeaconParser.parse("{\"magic\":\"OTHER_APP\"}", "192.168.1.1"))
    }

    @Test
    fun clientIsolation_exceptionMessage_matchesSpecification() {
        val ex = com.secondscreen.client.transport.ClientIsolationException()
        val expected = "Ağ cihazlar arası iletişimi engelliyor olabilir (Yurt/Okul Wi-Fi). Lütfen USB veya Mobil Etkin Nokta modunu seçin."
        assertEquals(expected, ex.message)
    }
}

